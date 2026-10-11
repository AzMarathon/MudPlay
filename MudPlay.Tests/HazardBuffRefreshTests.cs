using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Game.Stealth;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A hazard counter's buff (the desert waterskin) is tracked quietly and raised again
// shortly before it runs out, not sooner, and only where it matters (user,
// 2026-10-10). Inside its window the `use` is sent by the between-round cast pass,
// which holds it for sneak keeping like any buff; at the last call it goes out where
// the character stands.
public sealed class HazardBuffRefreshTests : IDisposable
{
    private readonly string _root;

    public HazardBuffRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-hazard-buff-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly RoomKey Oasis = new(1, 1);
    private static readonly RoomKey Dunes = new(1, 2);
    private static readonly RoomKey DeepDesert = new(1, 3);

    private const int Waterskin = 60;
    private const int BuffSpell = 300;
    private const int HeatSpell = 712;
    private const string SwigLine = "You take a swig of water from your waterskin.";
    private const string ThirstLine = "You suffer in the desert heat... you need water, soon!";

    // The buff lasts 1800 s, as the waterskin's does: its window opens at 1740 s and
    // its last call comes at 1785 s.
    private const int Lasts = 1800;

    private static Room Desert(RoomKey key) => new()
    {
        Key = key, Name = "Scorching Desert", Spell = 700, Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static readonly RoomHazardIndex.BuffCounter Counter =
        new(BuffSpell, HeatSpell, Lasts, new[] { Waterskin }, Array.Empty<int>());

    private static readonly RoomHazardIndex.RoomHazard Heat =
        new(new IReadOnlyList<int>[] { new[] { Waterskin } }, new[] { Counter });

    // The provisioner with everything around it a test can turn.
    private sealed class Field
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public int Carried = 1;
        public int? Charges;
        public bool WalkActive = true;
        public bool Following;
        public bool SneakKept;
        public bool SendBlocked;
        public bool AutoSneak = true;
        public List<RoomKey> RoomsAhead = new() { Dunes };
        public List<string> Wire { get; } = new();
        public int UsesSent;
        public int SneakSpent;
        public LogService Log { get; } = new();
        public AutoModeController Switch { get; }
        public AutoHazardCounterProvisioner Engine { get; }

        public Field()
        {
            Engine = new AutoHazardCounterProvisioner(
                resolveRoom: key => key == Dunes || key == DeepDesert ? Desert(key) : null,
                hazardForSpell: spell => spell == 700 ? Heat : null,
                carriedCount: id => id == Waterskin ? Carried : 0,
                itemName: id => id == Waterskin ? "waterskin" : null,
                messageMatcherForSpell: spell => spell switch
                {
                    BuffSpell => line => line.Contains("swig of water", StringComparison.OrdinalIgnoreCase),
                    HeatSpell => line => line.Contains("you need water", StringComparison.OrdinalIgnoreCase),
                    _ => null,
                },
                walkActive: () => WalkActive,
                now: () => Now,
                log: Log,
                followingLeader: () => Following,
                roomsAhead: () => RoomsAhead,
                allCounters: () => new[] { Counter },
                sneakKept: () => SneakKept,
                chargesLeft: _ => Charges,
                sendBlocked: () => SendBlocked,
                autoSneakOn: () => AutoSneak);
            Engine.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Engine.UseSent += () => UsesSent++;
            Engine.SneakSpentHere += () => SneakSpent++;
            ProfileService profile = new();
            profile.LoadBlank();
            Switch = new AutoModeController(profile);
            Engine.MasterSwitchOff = () => Switch.Blocks("Hazard counter item");
        }

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        public IReadOnlyList<string> LogLines => Log.Snapshot().Select(e => e.Message).ToList();
    }

    // ----- tracking -------------------------------------------------------

    // The buff's own line starts its clock wherever it shows, for a `use` typed by
    // hand as for one of ours, so the step into the desert spends nothing more.
    [Fact]
    public void BuffLine_SeenAnywhere_StartsTheClock()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);            // typed by hand, far from the desert

        f.Advance(600);
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Empty(f.Wire);
        Assert.Null(f.Engine.DueNow());
    }

    // A death wipes it, and across a dropped link or a new profile nothing can be
    // vouched for: unknown counts as off when a hazard room is next.
    [Fact]
    public void Forget_CountsTheBuffAsOff_WhenAHazardRoomIsNext()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        Assert.Single(f.Wire);

        f.Engine.Forget("death");
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(2, f.Wire.Count);
        Assert.Contains(f.LogLines, l => l.Contains("buff tracking reset (death)"));
    }

    [Fact]
    public void Tracking_IsDescribed_ForTheBugReport()
    {
        Field f = new();
        Assert.Empty(f.Engine.DescribeTracking());

        f.Engine.OnApproachingRoom(Dunes);
        string sent = Assert.Single(f.Engine.DescribeTracking());
        Assert.Contains("buff 300: on since", sent);
        Assert.Contains("a `use` went out; its line not seen", sent);
        Assert.Contains("in 1800 s", sent);
        Assert.Contains("refresh window 60 s, last call 15 s", sent);

        f.Engine.OnServerLine(SwigLine);
        Assert.Contains("its line was seen", Assert.Single(f.Engine.DescribeTracking()));
    }

    // ----- timing: before it wears off, but not too soon ---------------------

    // The window buys the search for a room with no NPCs, so it comes with
    // Auto-Sneak. Without it the refresh keeps to the last 15 s, and then it can't
    // wait.
    [Theory]
    [InlineData(1740, false)]
    [InlineData(1784, false)]
    [InlineData(1785, true)]
    [InlineData(1900, true)]
    public void AutoSneakOff_TheRefreshIsDueOnlyAtTheLastCall(int secondsOn, bool due)
    {
        Field f = new() { AutoSneak = false };
        f.Engine.OnServerLine(SwigLine);

        f.Advance(secondsOn);

        Assert.Equal(due ? ("use waterskin", true) : null, f.Engine.DueNow());
    }

    // Nor is anything looked ahead for: with the desert a step away and the buff off
    // nothing is on offer, and the first drink goes out at the edge, on the step.
    [Fact]
    public void AutoSneakOff_NoLookAhead_TheFirstDrinkGoesOutAtTheEdge()
    {
        Field f = new() { AutoSneak = false, RoomsAhead = new() { Oasis, Dunes } };

        Assert.Null(f.Engine.DueNow());
        Assert.False(f.Engine.FireDue(sneakKept: false));
        Assert.Empty(f.Wire);

        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Contains(f.LogLines, l => l.Contains("refreshed in its last 15 s (Auto-Sneak off"));
    }

    // Standing in the desert with Auto-Sneak on and the buff up: nothing is due until
    // 60 s before it ends, it can wait until 15 s before, and then it can't.
    [Theory]
    [InlineData(1739, false, false)]
    [InlineData(1740, true, false)]
    [InlineData(1784, true, false)]
    [InlineData(1785, true, true)]
    [InlineData(1900, true, true)]
    public void InTheDesert_TheRefreshIsDueOnlyInsideItsWindow(int secondsOn, bool due, bool mustGoNow)
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Advance(secondsOn);

        (string What, bool MustGoNow)? offer = f.Engine.DueNow();
        Assert.Equal(due, offer is not null);
        if (offer is { } o)
        {
            Assert.Equal("use waterskin", o.What);
            Assert.Equal(mustGoNow, o.MustGoNow);
        }
    }

    // Outside hazard country nothing is refreshed at all, whatever the clock says.
    [Fact]
    public void NoCounteredRoomHereOrAhead_NothingIsDue()
    {
        Field f = new() { RoomsAhead = new() { Oasis } };
        Assert.Null(f.Engine.DueNow());             // off, and it doesn't matter here

        f.Engine.OnServerLine(SwigLine);
        f.Advance(1790);
        Assert.Null(f.Engine.DueNow());             // about to run out, and it doesn't matter here
    }

    // A countered room a few planned steps ahead makes it matter. With the buff off
    // the `use` is due, and can wait for a good moment: the step into the desert is
    // its own last call.
    [Fact]
    public void CounteredRoomAhead_BuffOff_IsDue_AndCanWait()
    {
        Field f = new() { RoomsAhead = new() { Oasis, Oasis, Dunes } };

        (string What, bool MustGoNow)? offer = f.Engine.DueNow();

        Assert.Equal(("use waterskin", false), offer);
        Assert.Contains(f.LogLines, l => l.StartsWith("due: `use waterskin` — the buff is off or not known to be on"));
    }

    // Standing in a hazard room with nothing running is the player's own business.
    [Fact]
    public void NoWalkAndNotFollowing_NothingIsDue()
    {
        Field f = new() { WalkActive = false };
        Assert.Null(f.Engine.DueNow());

        f.Following = true;
        Assert.NotNull(f.Engine.DueNow());
    }

    // One `use` per window: once it has gone out nothing more is due until the next.
    [Fact]
    public void OneUsePerWindow()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Advance(1750);

        Assert.True(f.Engine.FireDue(sneakKept: false));
        Assert.Null(f.Engine.DueNow());
        Assert.False(f.Engine.FireDue(sneakKept: false));
        f.Engine.OnApproachingRoom(DeepDesert);

        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Equal(1, f.UsesSent);
        Assert.Contains(f.LogLines, l => l.StartsWith("used `use waterskin` for buff 300 1/2 (Scorching Desert)"));
    }

    // The step into a countered room is the last call: with time still on the buff
    // nothing goes out, and inside the last 15 s it does.
    [Fact]
    public void StepIntoACounteredRoom_UsesOnlyAtTheLastCall()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Advance(1750);                            // inside the window, not at the last call
        f.Engine.OnApproachingRoom(DeepDesert);
        Assert.Empty(f.Wire);

        f.Advance(40);                              // 10 s left
        f.Engine.OnApproachingRoom(DeepDesert);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
    }

    // ----- a `use` the game turns away ---------------------------------------

    // The round refusal puts the clock back where it was, so the `use` is due again
    // and goes out a round later.
    [Fact]
    public void RoundRefusal_PutsTheClockBack_AndTheUseIsDueAgain()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Advance(1750);
        Assert.True(f.Engine.FireDue(sneakKept: false));

        f.Engine.OnServerLine("You have already cast a spell this round!");

        Assert.NotNull(f.Engine.DueNow());
        Assert.Contains(f.LogLines, l => l.Contains("refused: `use waterskin` for buff 300 — already cast this round"));
        Assert.True(f.Engine.FireDue(sneakKept: false));
        Assert.Equal(2, f.Wire.Count);
    }

    // The game says the item isn't there, or is spent: asking again would only draw
    // the same line, so nothing more is sent until the pack changes.
    [Theory]
    [InlineData("You don't have waterskin.")]
    [InlineData("There are no more uses in waterskin.")]
    [InlineData("You may not use that item!")]
    public void RefusalThatHolds_SendsNothingMore_UntilThePackChanges(string refusal)
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);

        f.Engine.OnServerLine(refusal);
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Null(f.Engine.DueNow());
        Assert.Single(f.Wire);
        Assert.Contains(f.LogLines, l => l.Contains("refused: `use waterskin`") && l.Contains("not sent again until the pack changes"));

        f.Carried = 2;                              // another one picked up
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(2, f.Wire.Count);
    }

    // "You don't have ..." answers other commands as well: only one naming the item
    // is taken for the answer to our `use`.
    [Fact]
    public void ARefusalNamingSomethingElse_IsNotOurs()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);

        f.Engine.OnServerLine("You don't have 50 gold crowns to drop!");
        f.Advance(60);
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Single(f.Wire);                      // still counted as on
        Assert.Null(f.Engine.DueNow());
    }

    // The client's own count says the item is empty: nothing is sent.
    [Fact]
    public void NoChargesLeft_IsNotSent()
    {
        Field f = new() { Charges = 0 };

        f.Engine.OnApproachingRoom(Dunes);
        Assert.False(f.Engine.FireDue(sneakKept: false));

        Assert.Empty(f.Wire);
        Assert.Single(f.LogLines, l => l.Contains("has no charges left for buff 300"));
    }

    // With the send gate up a `use` would be dropped, so none is sent or stamped,
    // and it goes out once the gate is down.
    [Fact]
    public void SendGateUp_NothingIsSentOrStamped()
    {
        Field f = new() { SendBlocked = true };
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Null(f.Engine.DueNow());
        Assert.Empty(f.Wire);

        f.SendBlocked = false;
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Single(f.Wire);
    }

    // ----- the master switch -------------------------------------------------

    [Fact]
    public void SwitchOff_TheDueUseIsNotFired_AndNothingIsStamped()
    {
        Field f = new();
        f.Switch.TurnOff();

        Assert.False(f.Engine.FireDue(sneakKept: false));
        Assert.Empty(f.Wire);
        Assert.Equal(1, f.Switch.SkippedSinceOff["Hazard counter item"]);

        f.Switch.TurnOn();
        Assert.True(f.Engine.FireDue(sneakKept: false));
        Assert.Single(f.Wire);
    }

    // ----- a kept sneak --------------------------------------------------------

    // The last call goes out though a sneak is being kept, and says the sneak is
    // spent here, for whatever reacts to a lost sneak.
    [Fact]
    public void LastCall_OverAKeptSneak_GoesOut_AndReportsTheSneakSpent()
    {
        Field f = new() { SneakKept = true };

        f.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Equal(1, f.SneakSpent);
        Assert.Contains(f.LogLines, l => l.StartsWith("forced: `use waterskin` for buff 300 before 1/2 (Scorching Desert)"));
    }

    [Fact]
    public void LastCall_WithNoSneakKept_ReportsNothing()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(0, f.SneakSpent);
    }

    // ----- sent by the between-round cast pass -------------------------------

    private sealed class Pass : IDisposable
    {
        public Field Field { get; } = new();
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public SpellsSettings Spells { get; } = new();
        public HealthSettings Health { get; } = new();
        public List<string> SpellsCast { get; } = new();
        public bool AutoHeal;
        public bool SneakKept;
        public int CastFired;

        public Pass()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, new LogService());
            Cast.SetWireSender(_ => { });
            Cast.CastSent += SpellsCast.Add;
            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells, readHealth: () => Health, isEnabled: () => AutoHeal, log: new LogService());
            Director.SetAutoBlessGate(() => false);
            Director.SetStealthMaintenanceDeferGate(() => SneakKept);
            Director.CastFired += () => CastFired++;
            Field.Engine.UseSent += Director.NoteClientUseSent;
            State.MaxHp = 200;
            State.Hp = 200;
            State.HasPromptData = true;
            // Wired last: each prompt value above runs a pass of its own.
            Director.SetClientUseSource(Field.Engine.DueNow, Field.Engine.FireDue, Field.Engine.NoteHeldForSneak);
        }

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    // The hazard buff answers to neither Auto-Heal nor Auto-Bless: with both off the
    // pass still sends it, as the round's one cast, and the cast's followers run.
    [Fact]
    public void Pass_SendsTheDueUse_WithAutoHealAndAutoBlessOff()
    {
        using Pass p = new();

        Assert.Equal("use waterskin", p.Director.Evaluate());

        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(1, p.CastFired);
        Assert.Null(p.Director.Evaluate());         // the round's cast is spent
        Assert.Single(p.Field.Wire);
    }

    // With a sneak being kept the `use` waits like any buff: the pass holds it, the
    // walk is told a cast is waiting for a room with no NPCs, and there it goes out.
    [Fact]
    public void Pass_SneakKept_HoldsTheUse_UntilARoomWithNoNpcs()
    {
        using Pass p = new() { SneakKept = true };
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1750);                      // inside the window, not at the last call

        Assert.Null(p.Director.Evaluate());
        Assert.Empty(p.Field.Wire);
        Assert.True(p.Director.HasSneakHeldCast);
        Assert.Contains(p.Field.LogLines, l => l.StartsWith("waiting for a room with no NPCs"));

        p.SneakKept = false;                        // a room with no NPCs
        Assert.Equal("use waterskin", p.Director.Evaluate());
        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(0, p.Field.SneakSpent);
        Assert.False(p.Director.HasSneakHeldCast);
    }

    // No room with no NPCs turned up before the last call: it goes out where the
    // character stands, sneak or not, and the sneak is reported spent.
    [Fact]
    public void Pass_SneakKept_AtTheLastCall_SendsItAnyway()
    {
        using Pass p = new() { SneakKept = true };
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1790);

        Assert.Equal("use waterskin", p.Director.Evaluate());

        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(1, p.Field.SneakSpent);
        Assert.Contains(p.Field.LogLines, l => l.StartsWith("forced: `use waterskin` for buff 300 1/2 (Scorching Desert)"));
    }

    // An emergency heal that is due takes the round ahead of it.
    [Fact]
    public void Pass_AnEmergencyHealGoesFirst()
    {
        using Pass p = new() { AutoHeal = true };
        p.Spells.EmergencyHealSpell = "lastresort";
        p.Health.EmergencyHealTrigger = 20;
        p.State.Hp = 20;                            // 10% of 200

        p.Director.Evaluate();

        Assert.Equal(new[] { "lastresort" }, p.SpellsCast);
        Assert.Empty(p.Field.Wire);
    }

    // A `use` sent by another road (the step into the desert) is the round's cast
    // all the same: nothing else is cast in that round.
    [Fact]
    public void UseSentAtTheStep_TakesTheRoundsCast()
    {
        using Pass p = new() { AutoHeal = true };
        p.Spells.MinorHealSpell = "heal";
        p.Health.MinorHealCombatTrigger = 90;
        p.Field.RoomsAhead = new() { Oasis };       // nothing on offer to the pass itself

        p.Field.Engine.OnApproachingRoom(Dunes);
        p.State.Hp = 100;                           // a minor heal is due

        Assert.Equal(1, p.CastFired);
        Assert.Empty(p.SpellsCast);
    }

    // ----- the step into the desert, with Auto-Sneak on ------------------------

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Oasis",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Scorching Desert",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The last call is asked ahead of the sneak check, so the `sn` behind the `use`
    // is the held kind: the step waits for its answer and goes out sneaked.
    [Fact]
    public void Walker_LastCallAheadOfTheSneakCheck_UseThenSnAnswered_ThenTheStep()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        tracker.SetLocated(Oasis);
        MovementCoordinator coordinator = new();
        AutoWalkManager walker = new(graph, new BfsMapper(graph), tracker, coordinator);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        StealthManager stealth = new(router, new PlayerState(), new LogService());
        stealth.SetAutoToggles(() => true, () => false);
        stealth.SetEngineDrivingCheck(() => true);
        stealth.SetMovementCoordinator(coordinator);
        void Feed(string line) => router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        Field f = new();
        List<string> wire = new();
        void Send(byte[] bytes)
        {
            string command = Encoding.Latin1.GetString(bytes).TrimEnd('\r');
            wire.Add(command);
            stealth.NoteCommandSent(command, () => false, items: null);
        }
        f.Engine.SetWireSender(Send);
        f.Engine.UseSent += stealth.ReSneakAfterCast;   // what CastFired does in the app
        stealth.SetWireSender(Send);
        walker.SetWireSender(Send);
        walker.SetMoveReadyCheck(() =>
        {
            f.Engine.OnApproachingRoom(Dunes);
            return stealth.ReadyToMoveSneaking();
        });
        walker.SetApproachRoomHook(f.Engine.OnApproachingRoom);
        walker.SetPreMoveHook(stealth.RequestPreMoveStealth);
        Feed("Sneaking...");

        Assert.True(walker.WalkTo(Dunes));
        Assert.Equal(new[] { "use waterskin", "sn" }, wire);   // the step waits on the `sn`
        Assert.True(stealth.IsHoldingForSneakAnswer);

        Feed("Attempting to sneak...");

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, wire);
    }
}
