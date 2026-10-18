using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Attack Not Last and the follow Target Priorities hold only a room's opening: once
// the fight in a room is under way, a new pick there goes out at once. These tests pin
// which room the fight on record belongs to when we change rooms without a move of our
// own (a teleport, a drag with no drag line, a fear run, a death, a relog), driving
// the wire through the terminal, the line extractor, the router, the room display
// parser and the room tracker as the client runs them.
public sealed class CombatManagerRoomChangeHoldTests
{
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Volcano Tunnel",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lava Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Obsidian Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Ash Pit",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Crater Floor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/4" },
          { "Map Number": 1, "Room Number": 7, "Name": "Crater Floor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/3" },
          { "Map Number": 1, "Room Number": 8, "Name": "Dark Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/9", "S": "1/1", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dark Forest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/8", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Obsidian Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly TerminalEmulator _terminal;
        // The one clock every component here reads, the terminal's line stamps
        // included; nothing waits on real time. Each flush from the server lands a
        // millisecond after the last, and each read is a tick on from the one
        // before. The steps the engine orders by time (a room left, the next room's
        // roster read, a hold armed) come from different lines of a flush, and the
        // real clock always reads those apart.
        public ManualClock Clock { get; } = new();
        private static readonly TimeSpan FlushGap = TimeSpan.FromMilliseconds(1);
        private DateTimeOffset ReadClock()
        {
            Clock.Advance(TimeSpan.FromTicks(1));
            return Clock.Now;
        }
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public LineExtractor Lines { get; }
        public RoomTracker Tracker { get; }
        public RoomDisplayParser Display { get; }
        public DeathDetector Deaths { get; }
        public RoomEntryWatcher Entry { get; }
        public MonsterDeathWatcher MonsterDeaths { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "pu",
            TargetOrder = TargetOrder.Normal,
            AttackTiming = AttackTiming.AttackNotLast,
        };
        public bool Feared { get; set; }

        // The holds' in-round deadline: the projected round is a test value, and the
        // one-shot waits until the test fires it.
        public TimeSpan? ToRound { get; set; } = TimeSpan.FromSeconds(3);
        private Action? _deadline;
        public int DeadlinesScheduled { get; private set; }

        // What the combat engine posts waits until the flush being fed is done, as
        // AppServices posts it through the UI dispatcher.
        private readonly List<Action> _posted = new();
        private void DrainPosted()
        {
            while (_posted.Count > 0)
            {
                List<Action> due = new(_posted);
                _posted.Clear();
                foreach (Action a in due) a();
            }
        }

        public DarkRoomCombatWatcher DarkRoom { get; }

        public Harness(string rooms = Rooms)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-roomchange-hold-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph, log: null, now: ReadClock);
            Tracker.SetFearProbe(() => Feared);

            // Wired in the order the client wires them: the router takes every line
            // first, then the room display parser reads the same lines.
            DefaultPatterns.Seed(Router);
            _terminal = new TerminalEmulator(80, 25, ReadClock);
            Lines = new LineExtractor(_terminal);
            Lines.LineEmitted += Router.Dispatch;
            Display = new RoomDisplayParser(Lines, Tracker, Log);
            Deaths = new DeathDetector(Tracker, Log);
            Deaths.AttachLineExtractor(Lines);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Tracker, Log, now: ReadClock);
            // Built ahead of the combat engine, as in AppServices: a kill drops the
            // monster from the roster through it.
            MonsterDeaths = new MonsterDeathWatcher(Router, Log, now: ReadClock);
            MonsterDeaths.MonsterDied += evt =>
            {
                if (evt.RoomSpellRoster is null) Combat!.NoteUnattributedDeath();
            };
            Classifier.AttachLineExtractor(Lines);
            Entry = new RoomEntryWatcher(Router, Classifier, Log, now: ReadClock);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: _ => new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => true,
                readOwnGivenName: () => "MudPlay",
                post: _posted.Add,
                log: Log,
                now: ReadClock);
            DarkRoom = new DarkRoomCombatWatcher(Router, Tracker, Classifier,
                currentTarget: () => Combat.CurrentTarget, now: ReadClock, log: Log);
            Router.Subscribe(KnownPatterns.RoomVeryDark, _ => Tracker.NoteDarkRoomEntered());
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.RoomSpellKill += MonsterDeaths.NoteRoomSpellKill;
            Combat.SetFearGate(() => Feared);
            Combat.SetHoldDeadline(() => ToRound, (_, due) => { _deadline = due; DeadlinesScheduled++; });
            Combat.SetConfirmedRoomProbe(() =>
                Tracker.State.Confidence == RoomConfidence.Confirmed ? Tracker.State.CurrentRoom : null);
            // As AppServices and the main window wire them.
            Tracker.StateChanged += t =>
            {
                if (t.NewRoom?.Key != t.PreviousRoom?.Key) Combat.NoteRoomLeft(Tracker.LastMoveSentAt);
            };
            Display.RoomParsed += obs =>
            {
                if (!Tracker.IsPeekSuppressed()) Combat.NoteRoomDisplayed(obs);
            };
            Tracker.PlayerDeathObserved += () => Classifier.NoteRoomChanged();
            Tracker.PlayerDeathObserved += () => Combat.OnPlayerDeath();

            Party.IsInParty = true;
            foreach (string n in new[] { "MudPlay", "Cidir NineteenTr", "Thresh II" })
                Party.Members.Add(new PartyMember { Name = n });
            Party.LeaderName = "Cidir NineteenTr";
            Players.RecordObservation("Cidir", "Priest", null, null, null, null, null, DateTime.UtcNow);
            Players.RecordObservation("Thresh", "Thief", null, null, null, null, null, DateTime.UtcNow);
            AddMonster(635, "tunnel horror");
            AddMonster(636, "giant skeleton");
            AddMonster(637, "cave bear");
        }

        private void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));

        // One flush from the server; what it posted runs once it has been read.
        public void Wire(params string[] lines)
        {
            Clock.Advance(FlushGap);
            _terminal.Feed(Encoding.Latin1.GetBytes(string.Concat(lines.Select(l => l + "\r\n"))));
            DrainPosted();
        }

        public void FireDeadline()
        {
            _deadline?.Invoke();
            DrainPosted();
        }

        // A room display as the game prints it: the bright-cyan name, who is here,
        // and the exits line the tracker places the room on.
        public void ShowRoom(string name, string? alsoHere, string exits)
        {
            List<string> lines = new() { $"\u001b[1;36m{name}\u001b[0m" };
            if (alsoHere is not null) lines.Add($"Also here: {alsoHere}.");
            lines.Add($"Obvious exits: {exits}.");
            Wire(lines.ToArray());
        }

        // Our own step: the walker tells the tracker, and the outbound observer the
        // combat engine, before the move's bytes go out.
        public void SendMove(Direction direction)
        {
            Tracker.NoteMoveSent(direction);
            Combat.NoteMoveSent();
        }

        public IEnumerable<string> AllSent => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));
        public int Punches => AllSent.Count(s => s.StartsWith("pu "));
        public string LastSent => Sent.Count == 0 ? "" : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose()
        {
            Entry.Dispose();
            DarkRoom.Dispose();
            Combat.Dispose();
            MonsterDeaths.Dispose();
            Classifier.Dispose();
            Deaths.Dispose();
            Display.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // We walk south into the Volcano Tunnel, where the party opens on the tunnel
    // horror, we attack behind them, and it dies: the fight there is under way.
    private static void FightInTheTunnel(Harness h)
    {
        h.ShowRoom("Lava Chamber", null, "south");
        h.SendMove(Direction.S);
        h.ShowRoom("Volcano Tunnel", "tunnel horror", "north");
        Assert.Equal(0, h.Punches);
        h.Wire("Thresh moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        // The kill that empties the room ends with *Combat Off* on both realms.
        h.Wire("*Combat Engaged*", "Thresh stabs tunnel horror for 50 damage!", "You gain 100 experience.",
            "*Combat Off*");
        Assert.Equal(1, h.Punches);
    }

    [Fact]
    public void Teleport_AfterAFight_NextRoomHoldsThenTheDeadlineAttacks()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Obsidian Hall", "giant skeleton", "east");     // no move of ours
        Assert.Equal(1, h.Punches);                                 // held for the party

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void Teleport_AfterAFight_PartyOpensTheNextRoom_WeAttackBehindThem()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Obsidian Hall", "giant skeleton, Thresh", "east");
        h.Wire("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        h.FireDeadline();
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void DraggedThroughFoliage_NoDragLine_NextRoomHolds()
    {
        // A text exit with a pass message moves a follower with no
        // " -- Following your Party leader <dir> -- " line (GAME_MECHANICS
        // "Following the leader: follow / drag movement").
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("You push through the dense foliage, and walk onto a small path.");
        h.ShowRoom("Ash Pit", "giant skeleton", "west");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void FearRun_NoMoveSent_NextRoomHolds()
    {
        // Fear runs us through an obvious exit with no move of ours and no line for it
        // (GAME_MECHANICS "Fear"); attacks wait for it to wear off.
        using Harness h = new();
        FightInTheTunnel(h);

        h.Feared = true;
        h.ShowRoom("Lava Chamber", "giant skeleton", "south");
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom!.Key);
        h.Feared = false;
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void DeathAndRespawn_TheRespawnRoomHolds()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("You have been killed!", "But, due to a miracle, you have been saved.", "You have 7 lives left.");
        h.ShowRoom("Temple, Halls of the Dead", "giant skeleton", "up");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void Relog_BackInTheSameRoom_HoldsForTheOpening()
    {
        // Out to the board's menu and back in: the fight on record is from before,
        // and the room shown on the way in is a new opening even though it is the same
        // room.
        using Harness h = new();
        FightInTheTunnel(h);

        h.Combat.NoteGameLeft();                                   // as AppServices does at the menu
        h.Classifier.NoteGameLeft();
        h.ShowRoom("Volcano Tunnel", "giant skeleton", "north");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void Reconnect_BackInTheSameRoom_HoldsForTheOpening()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Combat.OnDisconnected();                                 // as the disconnect handler does
        h.Classifier.NoteGameLeft();
        h.ShowRoom("Volcano Tunnel", "giant skeleton", "north");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void SameRoomShownAgain_FightUnderWay_PicksOnTheExitsLine()
    {
        // The room shown again after a kill is the room the fight is in: the next pick
        // goes out once the exits line places it, without waiting for the deadline.
        using Harness h = new();
        h.ShowRoom("Volcano Tunnel", "tunnel horror, giant skeleton", "north");
        h.Wire("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "You gain 100 experience.");     // our target dies

        h.Wire("\u001b[1;36mVolcano Tunnel\u001b[0m", "Also here: giant skeleton.");
        Assert.Equal(1, h.Punches);                                 // not yet placed
        h.Wire("Obvious exits: north.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);

        h.FireDeadline();
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void SameRoomShownAgain_FollowLeader_TakesTheLeadersTargetOnTheExitsLine()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.Default;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.ShowRoom("Volcano Tunnel", "tunnel horror, cave bear, giant skeleton, Cidir", "north");
        h.Wire("Cidir moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "Cidir smashes giant skeleton for 80 damage!", "You gain 100 experience.");

        h.Wire("\u001b[1;36mVolcano Tunnel\u001b[0m", "Also here: cave bear, giant skeleton, Cidir.");
        Assert.Equal(1, h.Punches);                                 // not yet placed
        h.Wire("Obvious exits: north.");
        Assert.Equal("pu giant skeleton", h.LastSent);              // the leader's, not our own cave bear
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void TeleportIntoARoomTheTrackerCantPlace_HoldsUntilTheDeadline()
    {
        // Two rooms share the name and exits, so nothing tells whether this is the room
        // the fight was in: the hold stands, and the deadline still attacks in the round.
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Crater Floor", "giant skeleton", "down");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void StepIntoTheNextRoom_StillHolds()
    {
        // The step with our own move, for contrast: the move sent drops the fight.
        using Harness h = new();
        FightInTheTunnel(h);

        h.SendMove(Direction.N);
        h.ShowRoom("Lava Chamber", "giant skeleton", "south");
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void BugReport_NamesARoomNotYetPlaced()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("\u001b[1;36mObsidian Hall\u001b[0m", "Also here: giant skeleton.");
        Assert.Contains("not yet placed", h.Combat.AttackOrderRoomState);
        h.Wire("Obvious exits: east.");
        Assert.Contains("fight under way here: no", h.Combat.AttackOrderRoomState);
    }

    // Two "Crater Floor" rooms alike in name and exits: the tracker can't place us,
    // so only the display itself says the room shown again is the room we were in.
    private static void FollowFightOnTheCraterFloor(Harness h)
    {
        h.Settings.AttackTiming = AttackTiming.Default;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.ShowRoom("Crater Floor", "tunnel horror, cave bear, giant skeleton, Cidir", "down");
        Assert.NotEqual(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        h.Wire("Cidir moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "Cidir smashes giant skeleton for 80 damage!", "You gain 100 experience.");
        Assert.Equal(1, h.Punches);
    }

    [Fact]
    public void TrackerCantPlace_SameRoomShownAgain_FollowTakesTheLeadersTargetAtOnce()
    {
        using Harness h = new();
        FollowFightOnTheCraterFloor(h);

        h.ShowRoom("Crater Floor", "cave bear, giant skeleton, Cidir", "down");
        Assert.Equal("pu giant skeleton", h.LastSent);              // the leader's, not our own cave bear
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void NoGameData_SameRoomShownAgain_PicksOnTheExitsLine()
    {
        // An unmapped area: no room the tracker knows, yet a re-display after a kill
        // still picks without waiting for the deadline.
        using Harness h = new(rooms: "[]");
        h.ShowRoom("Smoking Rift", "tunnel horror, giant skeleton", "north, south");
        h.Wire("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "You gain 100 experience.");

        h.ShowRoom("Smoking Rift", "giant skeleton", "north, south");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void NoGameData_TeleportToAnotherRoom_Holds()
    {
        using Harness h = new(rooms: "[]");
        h.ShowRoom("Smoking Rift", "tunnel horror", "north, south");
        h.Wire("Thresh moves to attack tunnel horror.", "*Combat Engaged*", "You gain 100 experience.");
        Assert.Equal(1, h.Punches);

        h.ShowRoom("Smoking Rift", "giant skeleton", "east");       // same name, other exits
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void FollowHoldNothingPlaced_DeadlineTakesTheLeadersTarget()
    {
        // The game always ends a display with its exits line; one lost or garbled leaves
        // the "Also here:" read with nothing to place it, so the pick waits for the
        // deadline, and still goes onto the leader's target.
        using Harness h = new();
        FollowFightOnTheCraterFloor(h);

        h.Wire("\u001b[1;36mCrater Floor\u001b[0m", "Also here: cave bear, giant skeleton, Cidir.");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void DeathIntoAnEmptyRoom_AWalkInStillHolds()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("You have been killed!", "But, due to a miracle, you have been saved.", "You have 7 lives left.");
        h.ShowRoom("Temple, Halls of the Dead", null, "up");
        h.Wire("A giant skeleton stomps in from above.");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void TeleportIntoAnEmptyRoom_AWalkInStillHolds()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Obsidian Hall", null, "east");
        h.Wire("A giant skeleton stomps in from the east.");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void FightSeenAfterAnUnplacedRead_IsThisRoomsFight()
    {
        // A read nothing placed holds the follow pick. A party member's hit seen after
        // it is this room's fight, so a monster walking in is picked at once, onto the
        // leader's last target.
        using Harness h = new();
        FollowFightOnTheCraterFloor(h);

        h.Wire("\u001b[1;36mCrater Floor\u001b[0m", "Also here: cave bear, giant skeleton, Cidir.");
        Assert.Equal(1, h.Punches);                                 // the follow hold
        h.Wire("Thresh stabs cave bear for 50 damage!");
        Assert.Equal(1, h.Punches);
        h.Wire("A tunnel horror stomps in from the east.");
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    // FollowLeader in the Volcano Tunnel: the leader opens on the tunnel horror, we
    // follow, the leader is seen hitting the giant skeleton, and the horror dies.
    private static void FollowFightInTheTunnel(Harness h, string exits)
    {
        h.Settings.AttackTiming = AttackTiming.Default;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.ShowRoom("Lava Chamber", null, "south");
        h.SendMove(Direction.S);
        h.ShowRoom("Volcano Tunnel", "tunnel horror, cave bear, giant skeleton, Cidir", exits);
        h.Wire("Cidir moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "Cidir smashes giant skeleton for 80 damage!", "You gain 100 experience.");
        Assert.Equal(1, h.Punches);
    }

    // The exits a room lists change in place, each with the line the game prints
    // for it (GAME_MECHANICS "Hidden exits — `sea <dir>` reveal wording", "Door and
    // gate barriers in the room display"), or as a `secret passage` that hides again
    // with no line: the room shown again with them changed is still this room.
    [Theory]
    [InlineData("north", "north, secret passage east", "You found an exit to the east!")]   // a search found it
    [InlineData("north, secret passage east", "north", null)]                                // it hid again
    [InlineData("north", "north, south", "The exit to the south just opened!")]              // a timed exit opened
    [InlineData("north, south", "north", "The exit to the south just closed!")]              // and shut
    [InlineData("north", "north, south", "A concealed passage opens to the south.")]         // a lever's passage
    public void SameRoomShownAgain_ExitsChangedInPlace_FollowTakesTheLeadersTargetAtOnce(
        string before, string after, string? line)
    {
        using Harness h = new();
        FollowFightInTheTunnel(h, before);

        if (line is not null) h.Wire(line);
        h.ShowRoom("Volcano Tunnel", "cave bear, giant skeleton, Cidir", after);
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void SameRoomShownAgain_PassageAnotherMemberFound_FollowTakesTheLeadersTargetAtOnce()
    {
        // Only the searcher is told what a search found; the rest of the room sees
        // that they searched (GAME_MECHANICS "Hidden exits — `sea <dir>` reveal
        // wording"). The passage then lists as a secret passage.
        using Harness h = new();
        FollowFightInTheTunnel(h, "north");

        h.Wire("Thresh is searching for exits.");
        h.ShowRoom("Volcano Tunnel", "cave bear, giant skeleton, Cidir", "north, secret passage east");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void AFindExcusesOnlyTheNextDisplay()
    {
        // A search's find explains the exit it found in the display that follows it;
        // a later display of the same name with that exit new again is another room.
        using Harness h = new();
        FollowFightInTheTunnel(h, "north");

        h.Wire("You found an exit to the south!");
        h.ShowRoom("Volcano Tunnel", "cave bear, giant skeleton, Cidir", "north");
        Assert.Equal("pu giant skeleton", h.LastSent);
        h.Wire("*Combat Engaged*", "You gain 100 experience.");

        h.ShowRoom("Volcano Tunnel", "cave bear, tunnel horror, Cidir", "north, south");
        Assert.Equal(2, h.Punches);                                 // held: nothing explains south now
        h.FireDeadline();
        Assert.Equal("pu cave bear", h.LastSent);
    }

    // The same name with exits nothing announced changing is another room.
    [Theory]
    [InlineData("north", "north, south")]                       // one more, nothing opened
    [InlineData("north, south", "north")]                       // one fewer, nothing shut
    [InlineData("north, secret passage east", "north, south")]  // the passage hid, but south is new
    public void SameName_ExitsChangedUnannounced_Holds(string before, string after)
    {
        using Harness h = new();
        FollowFightInTheTunnel(h, before);

        h.ShowRoom("Volcano Tunnel", "cave bear, giant skeleton, Cidir", after);
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu cave bear", h.LastSent);                 // a new opening: our own pick
    }

    [Fact]
    public void DraggedBetweenLikeNamedRooms_Holds()
    {
        // Pulled through a text exit from one "Dark Forest" to another whose exits are
        // among the first's: no line says an exit changed, so it's another room.
        using Harness h = new();
        h.ShowRoom("Lava Chamber", null, "south");
        h.SendMove(Direction.S);
        h.ShowRoom("Volcano Tunnel", null, "north");
        h.SendMove(Direction.N);
        h.ShowRoom("Dark Forest", "tunnel horror", "north, south, east");
        h.Wire("Thresh moves to attack tunnel horror.", "*Combat Engaged*", "You gain 100 experience.",
            "*Combat Off*");
        Assert.Equal(1, h.Punches);

        h.Wire("You push through the dense foliage, and walk onto a small path.");
        h.ShowRoom("Dark Forest", "giant skeleton", "north");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void TeleportIntoADeadEndOfTheSameName_Holds()
    {
        // A room with no exits lists "Obvious exits: NONE!!!" (GAME_MECHANICS "Room
        // display parsing — the room title is positional, not just bright cyan").
        using Harness h = new();
        h.ShowRoom("Lava Chamber", null, "south");
        h.SendMove(Direction.S);
        h.ShowRoom("Obsidian Hall", "tunnel horror", "east");
        h.Wire("Thresh moves to attack tunnel horror.", "*Combat Engaged*", "You gain 100 experience.",
            "*Combat Off*");
        Assert.Equal(1, h.Punches);

        h.Wire("\u001b[1;36mObsidian Hall\u001b[0m", "Also here: giant skeleton.", "Obvious exits: NONE!!!");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    // We step into a room too dark to show and fight there. The reveal's announce
    // can latch a not-last hold or end one, by which handler reads it first; the
    // deadline fired once covers both.
    private static void FightInTheDark(Harness h)
    {
        h.ShowRoom("Volcano Tunnel", null, "north");
        h.SendMove(Direction.N);
        h.Wire("The room is very dark - you can't see anything.");
        h.Wire("Thresh moves to attack tunnel horror.");           // reveals it in the dark
        h.FireDeadline();
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "You gain 100 experience.", "*Combat Off*");
    }

    [Fact]
    public void DarkRoomLitAfterTheFight_IsThisRoomsFight()
    {
        // The display that finally shows is the room the tracker has us in, not the
        // room shown before it.
        using Harness h = new();
        FightInTheDark(h);

        h.ShowRoom("Lava Chamber", "giant skeleton", "south");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void TeleportOutOfADarkFight_NextRoomHolds()
    {
        using Harness h = new();
        FightInTheDark(h);

        h.ShowRoom("Ash Pit", "giant skeleton", "west");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void DeathInADarkFight_AWalkInAtTheTempleHolds()
    {
        using Harness h = new();
        FightInTheDark(h);

        h.Wire("You have been killed!", "But, due to a miracle, you have been saved.", "You have 7 lives left.");
        h.ShowRoom("Temple, Halls of the Dead", null, "up");
        h.Wire("A giant skeleton stomps in from above.");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void ReleaseForARoomShownAgain_DoesNotEndTheNextRoomsHold()
    {
        // The room is shown again and its release is posted; in the same flush we're
        // teleported, and the next room's roster latches a hold of its own. The posted
        // release, run after the flush, is for the room before: it leaves that hold and
        // its deadline alone.
        using Harness h = new();
        FightInTheTunnel(h);
        int scheduled = h.DeadlinesScheduled;

        h.Wire("\u001b[1;36mVolcano Tunnel\u001b[0m", "Also here: cave bear.", "Obvious exits: north.",
            "\u001b[1;36mObsidian Hall\u001b[0m", "Also here: giant skeleton.");
        Assert.Equal(1, h.Punches);
        Assert.Equal(scheduled + 1, h.DeadlinesScheduled);          // the one hold's, not ended and re-armed
        h.Wire("Obvious exits: east.");
        Assert.Equal(1, h.Punches);
        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }
}
