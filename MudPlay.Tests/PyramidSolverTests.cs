using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Behavioural coverage for PyramidSolver against a simulated game: the harness
// plays the server's side of the climb through the real pyramid layout (PyramidRooms)
// — it moves the character, refuses a move with no exit or a shut gate, opens a gate
// when its block is pushed, and answers the sphinx — and reports each outcome to the
// room tracker the way the game's lines would. Timers are off; the pump fires the
// solver's pending wait whenever the game has nothing left to answer.
public sealed class PyramidSolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pyr_" + Guid.NewGuid().ToString("N"));

    private const string CeilingLine = "With a loud grinding noise, a concealed passage opens in the ceiling!";

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required MovementCoordinator Coord { get; init; }
        public required PyramidSolver Solver { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public List<string> SentText => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        public void Dispose() => Solver.Dispose();

        // ----- the simulated game -----
        public int Room = 1239;                      // where the character really is
        public int Processed;                        // commands the game has answered
        public HashSet<(int Room, string Dir)> OpenGates { get; } = new();
        public bool PlainDoorsOpen = true;           // F3's plain doors stand open
        public bool WaitDoorsOpen = true;            // so do its 1000-picklock doors
        public bool AllDark;                         // no floor shows its rooms
        public HashSet<(int Room, string Dir)> OpenedDoors { get; } = new();   // opened by the door manager
        public List<(int Room, Direction Dir, int Key)> DoorRequests { get; } = new();
        public bool DoorsJammed;                     // the door manager can open nothing
        public int SlamDoors;                        // the next N open doors shut as we step
        public bool LeaderHasKey;
        public int KeyRoomVisits;
        public int KeyDropsOnVisit = 1;              // the floating key drops on this visit to its room
        public bool MemberGrabsKey;                  // …into a member's pack, not ours
        public int IgnorePushes;                     // the next N push blocks do nothing
        public int RefuseMoves;                      // the next N moves are refused outright
        public int SilentMoves;                      // the next N moves land without a word
        public int LostMoves;                        // the next N moves never reach the game
        public int Refused;                          // moves the game refused
        public int ManualMoves;                      // moves the tracker read as hand-typed
        public int MostMovesWaiting;                 // most unanswered moves seen at once
        public List<string> PositionAsks { get; } = new();
        public Action<RoomKey>? PositionAnswer;      // the open `rm` request's reply
        public Action? PositionNoAnswer;
    }

    private Harness NewHarness(
        int encPercent = 0, EncumbranceLevel level = EncumbranceLevel.None,
        int quickness = 0, bool isParadigm = false, bool canDrive = true,
        string? leaderName = null, bool bindWire = true, bool solverEnabled = true,
        string[]? partyMembers = null)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), PyramidRooms.Json());
        File.WriteAllText(Path.Combine(dir, "Spells.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), "[]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        KnownSpellCatalog catalog = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo, catalog);
        graph.OnActiveSetChanged("alpha");

        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);

        InventorySnapshot snap = new(
            CurrencyHoldings.Empty,
            new EncumbranceReading(0, 100, encPercent, level),
            Array.Empty<EquippedItem>(), Array.Empty<string>(), DateTimeOffset.UtcNow);

        Harness? made = null;
        PyramidSolver solver = new(tracker, walker,
            snapshot: () => snap, quickness: () => quickness,
            log: null, useTimer: false, post: a => a(),
            isParadigm: () => isParadigm, canDrive: () => canDrive, leaderName: () => leaderName,
            enabled: () => solverEnabled, coordinator: coord,
            isPartyMember: n => partyMembers is not null
                && Array.Exists(partyMembers, m => string.Equals(m, n, StringComparison.OrdinalIgnoreCase)),
            askPosition: (reason, resolved, failed) =>
            {
                if (!isParadigm) return false;      // stock, no sysop: nothing to ask
                made!.PositionAsks.Add(reason);
                made.PositionAnswer = resolved;
                made.PositionNoAnswer = failed;
                return true;
            },
            openDoor: (doorDir, statRequirement, canBash, keyItemId, sender, reply) =>
            {
                Harness g = made!;
                g.DoorRequests.Add((g.Room, doorDir, keyItemId));
                bool opens = !g.DoorsJammed && (keyItemId > 0 ? g.LeaderHasKey : statRequirement < 1000);
                if (!opens)
                {
                    reply(new DoorOpenResult.Failed("test: it won't budge"));
                    return;
                }
                g.OpenedDoors.Add((g.Room, doorDir.ToString()));
                reply(DoorOpenResult.Opened.Instance);
            },
            holdsItem: id => id == PyramidScript.GoldenLionKeyItem && made!.LeaderHasKey);

        Harness h = new() { Tracker = tracker, Walker = walker, Coord = coord, Solver = solver };
        made = h;
        walker.SetWireSender(h.Sent.Add);
        walker.SetPyramidSolver(solver);
        walker.Event += h.Events.Add;
        tracker.ManualMoveObserved += () => h.ManualMoves++;
        // Through the outbound observer, as in the app: a move the solver didn't
        // announce to the tracker first would be read as hand-typed.
        OutboundMovementObserver outbound = new(tracker);
        if (bindWire)
            solver.SetWireSender(bytes =>
            {
                h.Sent.Add(bytes);
                outbound.ObserveOutbound(bytes);
            });
        return h;
    }

    private static void Locate(Harness h, int room)
    {
        h.Room = room;
        h.Tracker.SetLocated(new RoomKey(12, room));
    }

    private static void LocateFirepit(Harness h) => Locate(h, 1239);

    private static Harness Begin(Harness h, int room = 1239)
    {
        Locate(h, room);
        Assert.True(h.Solver.TryBegin(new RoomKey(12, 2085)));
        return h;
    }

    private static bool IsDoor(string cell) => cell.Contains("(Door") || cell.Contains("(Key");

    private static bool DoorOpen(Harness h, int room, string column)
    {
        string cell = PyramidRooms.ExitCells[room][column];
        if (h.OpenedDoors.Contains((room, column))) return true;
        if (cell.Contains("(Key")) return false;
        return cell.Contains("[1000") ? h.WaitDoorsOpen : h.PlainDoorsOpen;
    }

    // The room display the game would print for where the character stands.
    private static RoomObservation Observation(Harness h)
    {
        var cells = PyramidRooms.ExitCells[h.Room];
        HashSet<Direction> exits = new(), open = new(), shut = new();
        foreach ((string column, string cell) in cells)
        {
            Direction d = Enum.Parse<Direction>(column);
            if (PyramidRooms.Target(h.Room, d) is null) continue;
            if (cell.Contains("Hidden/Needs") && !h.OpenGates.Contains((h.Room, column))) continue;
            exits.Add(d);
            if (!IsDoor(cell)) continue;
            if (DoorOpen(h, h.Room, column)) open.Add(d); else shut.Add(d);
        }
        return new RoomObservation("Great Pyramid", exits, open, shut);
    }

    private static bool Lit(Harness h) => !h.AllDark && h.Room >= 2002;   // F1 and F2 are walked in the dark

    // The game answers the oldest unanswered command. Returns false when there was
    // none to answer.
    private static bool AnswerNext(Harness h)
    {
        List<string> sent = h.SentText;
        int waitingMoves = sent.Skip(h.Processed).Count(c => DirectionExtensions.TryFromToken(c, out _));
        h.MostMovesWaiting = Math.Max(h.MostMovesWaiting, waitingMoves);
        if (h.Processed >= sent.Count) return false;
        string cmd = sent[h.Processed++];

        if (DirectionExtensions.TryFromToken(cmd, out Direction dir))
        {
            if (h.LostMoves > 0) { h.LostMoves--; return true; }
            string column = dir.ToString();
            PyramidRooms.ExitCells[h.Room].TryGetValue(column, out string? cell);
            int? target = PyramidRooms.Target(h.Room, dir);
            bool gateShut = cell is not null && cell.Contains("Hidden/Needs") && !h.OpenGates.Contains((h.Room, column));
            bool isDoor = cell is not null && IsDoor(cell);
            bool doorShut = isDoor && !DoorOpen(h, h.Room, column);
            if (isDoor && !doorShut && h.SlamDoors > 0)
            {
                h.SlamDoors--;
                h.OpenedDoors.Remove((h.Room, column));
                if (!cell!.Contains("(Key") && !cell.Contains("[1000")) h.PlainDoorsOpen = false;
                doorShut = true;
            }
            if (target is null || gateShut || doorShut || h.RefuseMoves > 0)
            {
                if (h.RefuseMoves > 0) h.RefuseMoves--;
                h.Refused++;
                if (doorShut) h.Tracker.NoteDoorClosed();   // "The door is closed!"
                else h.Tracker.NoteMoveBlocked();
                return true;
            }
            h.Room = target.Value;
            if (h.SilentMoves > 0) { h.SilentMoves--; return true; }

            if (h.Room == PyramidScript.FloatingKeyRoom && ++h.KeyRoomVisits >= h.KeyDropsOnVisit
                && !h.LeaderHasKey)
            {
                // The floating key dies and its key is picked up as we walk in.
                if (h.MemberGrabsKey) h.Solver.FeedLineForTests("Jroc picks up golden lion key");
                else
                {
                    h.LeaderHasKey = true;
                    h.Solver.FeedLineForTests("You took golden lion key.");
                }
            }

            // In the dark the pitch-black line is the whole arrival, taken on the move
            // alone; a lit room prints itself, doors and all, a hop's time later.
            if (Lit(h)) h.Tracker.NoteRoomObserved(Observation(h), DateTimeOffset.UtcNow.AddSeconds(1));
            else h.Tracker.NoteDarkRoomEntered();
            return true;
        }

        if (cmd.StartsWith("@party give golden lion key to ", StringComparison.Ordinal))
        {
            if (h.MemberGrabsKey) h.LeaderHasKey = true;
            return true;
        }

        if (cmd == "push block")
        {
            if (h.IgnorePushes > 0) { h.IgnorePushes--; return true; }
            // "Action [on the N exit of room 12/1811]: push block, …"
            foreach (string cell in PyramidRooms.ExitCells[h.Room].Values.Where(c => c.Contains("push block")))
            {
                string[] words = cell.Split(' ', ']');
                string gateDir = words[Array.IndexOf(words, "the") + 1];
                int gateRoom = int.Parse(words[Array.IndexOf(words, "room") + 1].Split('/')[1]);
                h.OpenGates.Add((gateRoom, gateDir));
            }
            return true;
        }

        if (cmd.StartsWith("ask sphinx ", StringComparison.Ordinal))
        {
            Assert.Contains(h.Room, new[] { 1920, 2001, 2051 });   // only ever asked where a sphinx stands
            h.OpenGates.Add((h.Room, "U"));
            h.Solver.FeedLineForTests(CeilingLine);
            return true;
        }

        if (cmd == "look")
        {
            if (Lit(h)) h.Tracker.NoteRoomObserved(Observation(h), DateTimeOffset.UtcNow.AddSeconds(1));
            return true;
        }

        return true;   // get: nothing the solver waits on
    }

    // One turn of the world: the game answers a command, or — with nothing left to
    // answer — the solver's pending wait runs out.
    private static bool Pump(Harness h)
    {
        if (!h.Solver.Active) return false;
        if (AnswerNext(h)) return true;
        if (!h.Solver.HasSettlePendingForTests) return false;
        h.Solver.FireSettleForTests();
        return true;
    }

    private static void RunToEnd(Harness h, int maxIters = 6000)
    {
        int i = 0;
        while (i++ < maxIters && Pump(h)) { }
    }

    private static void RunUntil(Harness h, Func<bool> reached, int maxIters = 6000)
    {
        int i = 0;
        while (!reached() && i++ < maxIters && Pump(h)) { }
        Assert.True(reached(), "the climb never got there");
    }

    private static void DriveUntilFloor(Harness h, string floor) => RunUntil(h, () => h.Solver.FloorName == floor);

    private static void AssertFinished(Harness h)
    {
        Assert.False(h.Solver.Active);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished && e.Detail.Contains("arrived"));
        Assert.Equal(2085, h.Room);
        Assert.Equal(new RoomKey(12, 2085), h.Tracker.State.CurrentRoom!.Key);
    }

    // ----- pre-flight ------------------------------------------------

    [Fact]
    public void Preflight_StockHeavyLeader_Refuses()
    {
        using Harness h = NewHarness(encPercent: 80, level: EncumbranceLevel.Heavy, isParadigm: false);
        LocateFirepit(h);
        Assert.True(h.Solver.TryBegin(new RoomKey(12, 2085)));

        Assert.False(h.Solver.Active);
        WalkEvent last = h.Events[^1];
        Assert.Equal(WalkEventKind.Failed, last.Kind);
        Assert.Contains("pre-flight", last.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preflight_ParadigmTooEncumbered_Refuses()
    {
        // ~95% carry, no quickness → per-move well past 2s → > 5-min estimate.
        using Harness h = NewHarness(encPercent: 95, level: EncumbranceLevel.Heavy, quickness: 0, isParadigm: true);
        LocateFirepit(h);
        Assert.True(h.Solver.TryBegin(new RoomKey(12, 2085)));

        Assert.False(h.Solver.Active);
        Assert.Equal(WalkEventKind.Failed, h.Events[^1].Kind);
        Assert.Contains("too slow", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preflight_LightLeader_ProceedsAndEntersPyramid()
    {
        using Harness h = NewHarness(encPercent: 10, level: EncumbranceLevel.Light, isParadigm: true);
        Begin(h);

        Assert.True(h.Solver.Active);
        Assert.Equal("u", h.SentText[0]);   // entered the pyramid
    }

    // ----- CanSolve gating -------------------------------------------

    [Fact]
    public void CanSolve_RejectsNonPyramidAndUnavailable()
    {
        using Harness h = NewHarness();
        Assert.True(h.Solver.CanSolve(new RoomKey(12, 2085)));   // target
        Assert.True(h.Solver.CanSolve(new RoomKey(12, 1800)));   // a floor room
        Assert.False(h.Solver.CanSolve(new RoomKey(12, 335)));   // desert, not a floor
        Assert.False(h.Solver.CanSolve(new RoomKey(5, 2085)));   // wrong map

        using Harness noWire = NewHarness(bindWire: false);
        Assert.False(noWire.Solver.CanSolve(new RoomKey(12, 2085)));

        using Harness follower = NewHarness(canDrive: false);
        Assert.False(follower.Solver.CanSolve(new RoomKey(12, 2085)));

        using Harness disabled = NewHarness(solverEnabled: false);
        Assert.False(disabled.Solver.Enabled);
        Assert.False(disabled.Solver.CanSolve(new RoomKey(12, 2085)));
    }

    // ----- driving ---------------------------------------------------

    [Fact]
    public void FullClimb_WalksTheRealLayoutToTheTop()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Refused);                 // never walked into a wall or a shut gate
        Assert.Equal(0, h.Solver.AssumedLandings);
        Assert.Contains("ask sphinx fire", h.SentText);
        Assert.Contains("ask sphinx sun", h.SentText);
        Assert.Contains("ask sphinx stars", h.SentText);
        Assert.Equal(5, h.SentText.Count(t => t == "push block"));
    }

    [Fact]
    public void SendsOneMoveAndWaitsForItsAnswer()
    {
        using Harness h = Begin(NewHarness());
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.MostMovesWaiting);
    }

    [Fact]
    public void ItsOwnMoves_AreNeverReadAsHandTyped()
    {
        using Harness h = Begin(NewHarness());
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.ManualMoves);
    }

    [Fact]
    public void NoMoveGoesOutUntilTheLastOneIsAnswered()
    {
        using Harness h = Begin(NewHarness());
        Assert.Equal(new[] { "u" }, h.SentText);

        // Nothing answers: only the stall wait is pending, and no second move went out.
        Assert.Equal("AwaitingMove", h.Solver.PhaseName);
        Assert.Equal(new RoomKey(12, 1800), h.Solver.MoveInFlightTo);

        AnswerNext(h);                               // landed on F1
        Assert.Equal(new[] { "u", "s" }, h.SentText);   // F1 script starts s,w,n,...
    }

    [Fact]
    public void StartedPartWayAlongAFloor_ResumesAtTheStepScriptedFromThatRoom()
    {
        // 1853 is walked twice on F1 (out to the third block and back); a climb picked
        // up there takes the first pass, so the block still gets pushed.
        using Harness h = Begin(NewHarness(), room: 1853);

        IReadOnlyList<int> from = PyramidScript.FromRooms(PyramidFloor.F1)!;
        int first = from.ToList().IndexOf(1853);
        Assert.Equal(first + 1, h.Solver.ScriptStep);
        Assert.Equal(PyramidScript.Steps(PyramidFloor.F1)[first].Dir.ToToken(), h.SentText[0]);

        RunToEnd(h);
        AssertFinished(h);
    }

    // ----- refusals --------------------------------------------------

    [Fact]
    public void ShutGate_GoesBackToTheBlockAndPushesAgain()
    {
        using Harness h = Begin(NewHarness());
        h.IgnorePushes = 1;                          // the first push doesn't take
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.Refused);                  // the one bump into the shut gate
        Assert.Equal(1, h.Solver.GateRewinds);
        Assert.Equal(6, h.SentText.Count(t => t == "push block"));
    }

    [Fact]
    public void GateThatNeverOpens_FailsInsteadOfWalkingOn()
    {
        using Harness h = Begin(NewHarness());
        h.IgnorePushes = 100;
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Equal(WalkEventKind.Failed, h.Events[^1].Kind);
        Assert.Contains("gate", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1811, h.Room);                  // still at the first gate, not lost past it
    }

    [Fact]
    public void RefusedMove_IsSentAgainFromTheSameRoom()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);
        h.RefuseMoves = 2;
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(2, h.Refused);
    }

    [Fact]
    public void MoveRefusedOverAndOver_Fails()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);
        h.RefuseMoves = 100;
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Contains("refused", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1803, h.Room);
    }

    // ----- a move with no answer -------------------------------------

    [Fact]
    public void Stock_MoveWithNoAnswer_IsTakenAsLanded()
    {
        using Harness h = Begin(NewHarness(isParadigm: false));
        RunUntil(h, () => h.Room == 1803);
        h.SilentMoves = 1;                           // lands, but the tracker never hears of it
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.Solver.AssumedLandings);
        Assert.Empty(h.PositionAsks);                // stock has no `rm`
        Assert.Equal(0, h.Refused);
    }

    [Fact]
    public void Paradigm_MoveWithNoAnswer_AsksTheGameAndResendsWhenItNeverLanded()
    {
        using Harness h = Begin(NewHarness(isParadigm: true));
        RunUntil(h, () => h.Room == 1803);
        h.LostMoves = 1;                             // the move never reached the game
        RunUntil(h, () => h.PositionAsks.Count == 1);
        Assert.Equal("Resyncing", h.Solver.PhaseName);

        // `rm` says we never left 1803.
        h.Tracker.SetLocated(new RoomKey(12, 1803));
        h.PositionAnswer!(new RoomKey(12, 1803));
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Solver.AssumedLandings);
        Assert.Equal(0, h.Refused);
    }

    [Fact]
    public void Paradigm_MoveWithNoAnswer_CarriesOnWhenItDidLand()
    {
        using Harness h = Begin(NewHarness(isParadigm: true));
        RunUntil(h, () => h.Room == 1803);
        h.SilentMoves = 1;
        RunUntil(h, () => h.PositionAsks.Count == 1);

        h.Tracker.SetLocated(new RoomKey(12, h.Room));
        h.PositionAnswer!(new RoomKey(12, h.Room));
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Refused);
    }

    [Fact]
    public void Paradigm_UnansweredPositionRequest_FallsBackOnTheScript()
    {
        using Harness h = Begin(NewHarness(isParadigm: true));
        RunUntil(h, () => h.Room == 1803);
        h.SilentMoves = 1;
        RunUntil(h, () => h.PositionAsks.Count == 1);

        h.PositionNoAnswer!();
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.Solver.AssumedLandings);
    }

    [Fact]
    public void Paradigm_FoundOnALowerFloor_ClimbsAgainFromThere()
    {
        using Harness h = Begin(NewHarness(isParadigm: true, leaderName: "MudPlay"));
        DriveUntilFloor(h, "F4");
        RunUntil(h, () => h.Room == 2065);
        h.LostMoves = 1;
        RunUntil(h, () => h.PositionAsks.Count == 1);

        // A footpath fall: the game says we're back at floor 3's entry.
        Locate(h, 2002);
        h.PositionAnswer!(new RoomKey(12, 2002));
        Assert.Equal("F3", h.Solver.FloorName);
        RunToEnd(h);

        AssertFinished(h);
    }

    // ----- sphinx ----------------------------------------------------

    [Fact]
    public void Sphinx_IsAskedAgainWhenItStaysSilent_ThenTheClimbGivesUp()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.SentText.Contains("ask sphinx fire"));
        h.Processed = h.Sent.Count;                  // the game never answers the ask

        for (int i = 0; i < 10 && h.Solver.Active; i++)
        {
            h.Solver.FireSettleForTests();
            h.Processed = h.Sent.Count;
        }

        Assert.False(h.Solver.Active);
        Assert.Equal(4, h.SentText.Count(t => t == "ask sphinx fire"));   // the ask and three retries
        Assert.Contains("sphinx", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sphinx_PausedWhenItAnswers_GoesUpOnResumeWithoutAskingAgain()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.SentText.Contains("ask sphinx fire"));

        h.Coord.AssertGate(MovementCoordinator.UserGate, "test");
        for (int i = 0; i < 10; i++) Pump(h);        // the sphinx answers while paused
        Assert.True(h.Solver.IsHeld);
        Assert.Equal(1920, h.Room);                  // still below

        h.Coord.ClearGate(MovementCoordinator.UserGate, "test");
        RunUntil(h, () => h.Solver.FloorName == "F2");
        Assert.Equal(1, h.SentText.Count(t => t == "ask sphinx fire"));
    }

    [Fact]
    public void GoalOnTheRoute_EndsTheClimbThere()
    {
        using Harness h = NewHarness();
        Locate(h, 1239);
        Assert.True(h.Solver.TryBegin(new RoomKey(12, 1920)));   // the fire sphinx's room
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Equal(1920, h.Room);
        Assert.DoesNotContain("ask sphinx fire", h.SentText);
    }

    // ----- ends ------------------------------------------------------

    [Fact]
    public void Scatter_HaltsAndReportsFailure()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Solver.FloorName == "F1");

        h.Solver.OnRoomObserved(new RoomObservation("Scorched Cavern, Firepit",
            new HashSet<Direction> { Direction.U }));

        Assert.False(h.Solver.Active);
        Assert.Equal(WalkEventKind.Failed, h.Events[^1].Kind);
        Assert.Contains("scattered", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Death_EndsTheClimb()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);

        h.Tracker.NoteDeath(livesRemaining: 5);

        Assert.False(h.Solver.Active);
        Assert.Contains("died", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancel_StopsSendingWhereItStands()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);
        int sent = h.Sent.Count;

        h.Solver.Cancel("test");
        while (AnswerNext(h)) { }

        Assert.False(h.Solver.Active);
        Assert.Equal(sent, h.Sent.Count);
    }

    // ----- holds -----------------------------------------------------

    [Fact]
    public void UserPause_HoldsEvenTheTimedFloor_UntilResumed()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);

        h.Coord.AssertGate(MovementCoordinator.UserGate, "test");
        for (int i = 0; i < 30; i++) Pump(h);
        Assert.True(h.Solver.IsHeld);
        int room = h.Room;
        for (int i = 0; i < 30; i++) Pump(h);
        Assert.Equal(room, h.Room);                  // standing still

        h.Coord.ClearGate(MovementCoordinator.UserGate, "test");
        Assert.False(h.Solver.IsHeld);
        RunToEnd(h);
        AssertFinished(h);
    }

    [Fact]
    public void CombatAndRest_DoNotStopTheTimedAndChaosFloors()
    {
        using Harness h = Begin(NewHarness());
        DriveUntilFloor(h, "F1");
        h.Coord.AssertGate(MovementCoordinator.CombatGate, "test");
        h.Coord.AssertGate(MovementCoordinator.HealthRecoveryGate, "test");

        DriveUntilFloor(h, "F3");                    // straight through F1 and F2
        for (int i = 0; i < 30; i++) Pump(h);

        Assert.True(h.Solver.IsHeld);                // and held on the door floor
        Assert.Equal(2002, h.Room);
    }

    [Fact]
    public void Firepit_WaitsOutAFightOrRestBeforeStartingTheTimer()
    {
        using Harness h = NewHarness();
        h.Coord.AssertGate(MovementCoordinator.CombatGate, "test");
        Begin(h);

        for (int i = 0; i < 10; i++) Pump(h);
        Assert.True(h.Solver.IsHeld);
        Assert.Empty(h.Sent);                        // the entry `up` hasn't gone out

        h.Coord.ClearGate(MovementCoordinator.CombatGate, "test");
        Assert.Equal("u", h.SentText[0]);
    }

    [Theory]
    [InlineData(MovementCoordinator.CombatGate)]
    [InlineData(MovementCoordinator.HealthRecoveryGate)]
    [InlineData(MovementCoordinator.PartyWaitGate)]
    [InlineData(MovementCoordinator.AcquisitionGate)]
    public void PacedFloors_HoldForEveryGateTheWalkerWould(string gate)
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        DriveUntilFloor(h, "F3");

        h.Coord.AssertGate(gate, "test");
        for (int i = 0; i < 30; i++) Pump(h);
        Assert.True(h.Solver.IsHeld);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Finished);
        int room = h.Room;

        h.Coord.ClearGate(gate, "test");
        RunToEnd(h);
        AssertFinished(h);
        Assert.NotEqual(room, h.Room);
    }

    [Fact]
    public void SelfHeld_WaitsItOutOnAnyFloor()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);

        h.Solver.FeedLineForTests("Your legs are paralyzed!");
        for (int i = 0; i < 10; i++) Pump(h);       // well inside the wear-off cap
        Assert.True(h.Solver.IsHeld);

        h.Solver.FeedLineForTests("You can move again!");
        RunToEnd(h);
        AssertFinished(h);
        Assert.Equal(0, h.Refused);                  // no moves were thrown at the hold
    }

    [Fact]
    public void SelfHeld_WithItsWearOffLineMissed_IsLetGoAfterTheCap()
    {
        using Harness h = Begin(NewHarness());
        RunUntil(h, () => h.Room == 1803);

        h.Solver.FeedLineForTests("Your legs are paralyzed!");
        RunToEnd(h);                                 // "You can move again!" never arrives
        AssertFinished(h);
    }

    [Fact]
    public void PartyHold_PausesUntilFreed()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay", partyMembers: new[] { "Jroc", "Xian" }));
        DriveUntilFloor(h, "F3");

        // Undead priest holds a party member → stepping stalls. Check fewer ticks
        // than the ~20-tick "assume worn off" cap so we're verifying the pause, not
        // the cap's auto-release.
        h.Solver.FeedLineForTests("big undead priest casts hold person on Jroc!");
        for (int i = 0; i < 8; i++) Pump(h);
        Assert.True(h.Solver.Active);
        Assert.True(h.Solver.IsHeld);

        // A freedom cast on the held member releases the pause → finishes.
        h.Solver.FeedLineForTests("Xian casts freedom on Jroc!");
        RunToEnd(h);
        AssertFinished(h);
    }

    [Fact]
    public void PartyHold_IsTakenAsWornOffAfterTheCap()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay", partyMembers: new[] { "Jroc" }));
        DriveUntilFloor(h, "F3");

        h.Solver.FeedLineForTests("big undead priest casts hold person on Jroc!");
        RunToEnd(h);                                 // no freedom cast ever comes
        AssertFinished(h);
    }

    // ----- F3 doors ---------------------------------------------------

    [Fact]
    public void F3_OpenDoors_AreWalkedWithoutLookingOrOpening()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        RunToEnd(h);

        AssertFinished(h);
        Assert.DoesNotContain("look", h.SentText);
        // Only the key door needed the door manager.
        Assert.Equal(new[] { (2034, Direction.W, PyramidScript.GoldenLionKeyItem) }, h.DoorRequests);
    }

    [Fact]
    public void F3_ShutPlainDoors_GoToTheDoorManager()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.PlainDoorsOpen = false;
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Refused);                  // shown shut, so never walked into
        Assert.Contains((2002, Direction.E, 0), h.DoorRequests);
        Assert.DoesNotContain(h.SentText, t => t.StartsWith("bash", StringComparison.Ordinal));   // the manager's to send
    }

    [Fact]
    public void F3_WaitDoor_IsWaitedFor_NeverHandedToTheDoorManager()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.WaitDoorsOpen = false;
        RunUntil(h, () => h.Room == 2004 && h.Solver.PhaseName == "AwaitingDoor");

        for (int i = 0; i < 40; i++) Pump(h);
        Assert.Equal(2004, h.Room);
        Assert.Equal(0, h.Refused);
        Assert.DoesNotContain(h.DoorRequests, r => r.Room == 2004);
        Assert.Contains("look", h.SentText);         // the periodic refresh

        // The timer swings it open; the game says so.
        h.WaitDoorsOpen = true;
        h.Tracker.NoteNamedDoorOpened(Direction.N);
        h.Solver.FeedLineForTests("The door to the north just opened.");
        Assert.Equal("n", h.SentText[^1]);

        RunToEnd(h);
        AssertFinished(h);
    }

    [Fact]
    public void F3_WaitDoorThatNeverOpens_Fails()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.WaitDoorsOpen = false;
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Contains("never opened", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2004, h.Room);
    }

    [Fact]
    public void F3_DoorThatShutsAsWeStep_IsWorkedAgain()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        DriveUntilFloor(h, "F3");
        h.SlamDoors = 1;
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.Refused);
        Assert.Contains((2002, Direction.E, 0), h.DoorRequests);
    }

    [Fact]
    public void F3_DoorTheManagerCannotOpen_FailsWithItsReason()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.PlainDoorsOpen = false;
        h.DoorsJammed = true;
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Contains("won't budge", h.Events[^1].Detail);
        Assert.Equal(3, h.DoorRequests.Count(r => r.Room == 2002));
    }

    [Fact]
    public void F3_InTheDark_TheMoveItselfFindsOutAboutEachDoor()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.AllDark = true;
        h.PlainDoorsOpen = false;
        RunToEnd(h);

        AssertFinished(h);
        Assert.True(h.Refused >= 20);                // one bump per shut door, then the manager
        Assert.DoesNotContain("look", h.SentText);
    }

    [Fact]
    public void F3_InTheDark_AWaitDoorIsTriedAgainEveryFewSeconds()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.AllDark = true;
        h.WaitDoorsOpen = false;
        RunUntil(h, () => h.Room == 2004 && h.Refused == 3);

        h.WaitDoorsOpen = true;
        RunToEnd(h);
        AssertFinished(h);
    }

    [Fact]
    public void F3_AnchorsOnTheTrackedRoomInsteadOfWorkingTheWrongDoor()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.WaitDoorsOpen = false;
        RunUntil(h, () => h.Room == 2004 && h.Solver.PhaseName == "AwaitingDoor");   // step 3: the north wait door

        // We're really in 2006 (step 4 — the west door).
        Locate(h, 2006);
        h.WaitDoorsOpen = true;
        RunUntil(h, () => h.Room != 2006);

        Assert.Equal(2013, h.Room);                  // took step 4's west door
        Assert.Equal(0, h.Refused);
    }

    // ----- F3 golden lion key ----------------------------------------

    [Fact]
    public void Key_TakenByTheLeader_NoHandOverIsAsked()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Solver.KeyRespawns);
        Assert.DoesNotContain(h.SentText, t => t.StartsWith("@party give golden lion key", StringComparison.Ordinal));
        Assert.DoesNotContain("get golden lion key", h.SentText);
    }

    [Fact]
    public void Key_PickedUpByAMember_IsAskedForBeforeLeavingItsRoom()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay", partyMembers: new[] { "Jroc" }));
        h.MemberGrabsKey = true;
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(1, h.SentText.Count(t => t == "@party give golden lion key to MudPlay"));
        Assert.Equal(0, h.Solver.KeyRespawns);
    }

    [Fact]
    public void Key_NotDropped_StepsOutAndBackInUntilItDoes()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.KeyDropsOnVisit = 3;
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(2, h.Solver.KeyRespawns);
        Assert.Equal(3, h.KeyRoomVisits);
        Assert.Contains("get golden lion key", h.SentText);   // asked for it while waiting
    }

    [Fact]
    public void Key_NeverDropped_FailsAtTheKeyDoorSayingSo()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.KeyDropsOnVisit = 99;
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Equal(2034, h.Room);
        Assert.Equal(3, h.Solver.KeyRespawns);
        Assert.Contains("key door", h.Events[^1].Detail);
        Assert.Contains("golden lion key: not seen", h.Events[^1].Detail);
    }

    [Fact]
    public void Key_WaitsInItsRoomRatherThanWalkingOut()
    {
        using Harness h = Begin(NewHarness(leaderName: "MudPlay"));
        h.KeyDropsOnVisit = 99;
        RunUntil(h, () => h.Solver.PhaseName == "AwaitingKey");
        Assert.Equal(PyramidScript.FloatingKeyRoom, h.Room);

        // The key lands in our pack a few seconds in: the wait ends at once.
        Pump(h);
        Pump(h);
        h.LeaderHasKey = true;
        h.Solver.FeedLineForTests("You took golden lion key.");
        RunToEnd(h);

        AssertFinished(h);
        Assert.Equal(0, h.Solver.KeyRespawns);
        Assert.Equal(1, h.KeyRoomVisits);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }
}
