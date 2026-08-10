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

// Behavioural coverage for PyramidSolver: the pre-flight timer gate, CanSolve
// gating, the firepit entry + floor sequencing, the line-driven sphinx ascension,
// scatter fail-detection, and a full driven climb to 12/2085. Timers are off and
// the settle/line/observation seams are driven by hand.
public sealed class PyramidSolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pyr_" + Guid.NewGuid().ToString("N"));

    // Just the two rooms the solver's tracker locate touches (firepit start,
    // 12/2085 terminal); the climb itself dead-reckons off the canned script.
    private const string Rooms = """
        [
          { "Map Number": 12, "Room Number": 1239, "Name": "Scorched Cavern, Firepit",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 12, "Room Number": 2085, "Name": "Great Pyramid",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

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
    }

    private Harness NewHarness(
        int encPercent = 0, EncumbranceLevel level = EncumbranceLevel.None,
        int quickness = 0, bool isParadigm = false, bool canDrive = true,
        string? leaderName = null, bool bindWire = true, bool solverEnabled = true,
        string[]? partyMembers = null)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), Rooms);
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

        PyramidSolver solver = new(tracker, walker,
            snapshot: () => snap, quickness: () => quickness,
            log: null, useTimer: false, post: a => a(),
            isParadigm: () => isParadigm, canDrive: () => canDrive, leaderName: () => leaderName,
            enabled: () => solverEnabled, coordinator: coord,
            isPartyMember: n => partyMembers is not null
                && Array.Exists(partyMembers, m => string.Equals(m, n, StringComparison.OrdinalIgnoreCase)));

        Harness h = new() { Tracker = tracker, Walker = walker, Coord = coord, Solver = solver };
        walker.SetWireSender(h.Sent.Add);
        walker.SetPyramidSolver(solver);
        walker.Event += h.Events.Add;
        if (bindWire) solver.SetWireSender(h.Sent.Add);
        return h;
    }

    private static void LocateFirepit(Harness h) => h.Tracker.SetLocated(new RoomKey(12, 1239));

    private static readonly IReadOnlySet<Direction> AllDirs =
        new HashSet<Direction> { Direction.N, Direction.S, Direction.E, Direction.W, Direction.U, Direction.D };

    // Drive the climb: feed the sphinx cue when awaited, present every F3 door open,
    // otherwise fire the pending settle — until the solver goes inactive.
    private static void RunToEnd(Harness h, int maxIters = 6000)
    {
        int i = 0;
        while (h.Solver.Active && i++ < maxIters)
        {
            switch (h.Solver.PhaseName)
            {
                case "AwaitingSphinx":
                    h.Solver.FeedLineForTests("With a loud grinding noise, a concealed passage opens in the ceiling!");
                    break;
                case "AwaitingDoor":
                    h.Solver.OnRoomObserved(new RoomObservation("Great Pyramid", AllDirs, AllDirs));
                    break;
                default:
                    h.Solver.FireSettleForTests();
                    break;
            }
        }
    }

    // Drive the same loop but stop as soon as the solver reaches a target floor
    // (or goes inactive) — used to set up a mid-climb floor state.
    private static void DriveUntilFloor(Harness h, string floor, int maxIters = 3000)
    {
        int i = 0;
        while (h.Solver.Active && h.Solver.FloorName != floor && i++ < maxIters)
        {
            switch (h.Solver.PhaseName)
            {
                case "AwaitingSphinx":
                    h.Solver.FeedLineForTests("With a loud grinding noise, a concealed passage opens in the ceiling!");
                    break;
                case "AwaitingDoor":
                    h.Solver.OnRoomObserved(new RoomObservation("Great Pyramid", AllDirs, AllDirs));
                    break;
                default:
                    h.Solver.FireSettleForTests();
                    break;
            }
        }
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
        LocateFirepit(h);
        Assert.True(h.Solver.TryBegin(new RoomKey(12, 2085)));

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
    public void FirepitEntry_SendsUpThenFirstFloor1Move()
    {
        using Harness h = NewHarness();
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));   // sends `up`, schedules the F1 start
        h.Solver.FireSettleForTests();               // land on F1, drive step 0

        Assert.Equal("u", h.SentText[0]);
        Assert.Equal("s", h.SentText[1]);            // F1 script starts s,w,n,...
    }

    [Fact]
    public void Sphinx_AscendsOnCeilingOpenedBroadcast()
    {
        using Harness h = NewHarness();
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));

        // Fast-forward F1's moves/push-blocks until the solver asks the sphinx.
        int guard = 0;
        while (h.Solver.PhaseName != "AwaitingSphinx" && guard++ < 400)
            h.Solver.FireSettleForTests();
        Assert.Equal("AwaitingSphinx", h.Solver.PhaseName);
        Assert.Contains("ask sphinx fire", h.SentText);

        h.Solver.FeedLineForTests("With a loud grinding noise, a concealed passage opens in the ceiling!");
        Assert.Equal("u", h.SentText[^1]);          // ascended
    }

    [Fact]
    public void Scatter_HaltsAndReportsFailure()
    {
        using Harness h = NewHarness();
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));
        h.Solver.FireSettleForTests();               // on F1

        h.Solver.OnRoomObserved(new RoomObservation("Scorched Cavern, Firepit",
            new HashSet<Direction> { Direction.U }));

        Assert.False(h.Solver.Active);
        Assert.Equal(WalkEventKind.Failed, h.Events[^1].Kind);
        Assert.Contains("scattered", h.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullClimb_DeliversToTargetAndReportsSuccess()
    {
        using Harness h = NewHarness(leaderName: "MudPlay");
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));
        RunToEnd(h);

        Assert.False(h.Solver.Active);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished && e.Detail.Contains("arrived"));
        Assert.Equal(new RoomKey(12, 2085), h.Tracker.State.CurrentRoom!.Key);

        // The climb sent each sphinx word, all five push-blocks, and the key-door
        // consolidation.
        Assert.Contains("ask sphinx fire", h.SentText);
        Assert.Contains("ask sphinx sun", h.SentText);
        Assert.Contains("ask sphinx stars", h.SentText);
        Assert.Equal(5, h.SentText.Count(t => t == "push block"));
        Assert.Contains("@party give golden lion key to MudPlay", h.SentText);
    }

    // ----- combat / hold gating -------------------------------------

    [Fact]
    public void CombatGate_PausesPacedFloorSteppingUntilCleared()
    {
        using Harness h = NewHarness(leaderName: "MudPlay");
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));
        DriveUntilFloor(h, "F3");                     // through the blind floors to F3
        Assert.Equal("F3", h.Solver.FloorName);

        // Combat engaged on the paced floor → stepping stalls (no completion).
        h.Coord.AssertGate(MovementCoordinator.CombatGate, "test");
        for (int i = 0; i < 30; i++) RunOneStep(h);
        Assert.True(h.Solver.Active);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Finished);

        // Combat clears → the climb resumes and finishes.
        h.Coord.ClearGate(MovementCoordinator.CombatGate, "test");
        RunToEnd(h);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void PartyHold_PausesUntilFreed()
    {
        using Harness h = NewHarness(leaderName: "MudPlay", partyMembers: new[] { "Jroc", "Xian" });
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));
        DriveUntilFloor(h, "F3");

        // Undead priest holds a party member → stepping stalls. Check fewer ticks
        // than the ~20-tick "assume worn off" cap so we're verifying the pause, not
        // the cap's auto-release.
        h.Solver.FeedLineForTests("big undead priest casts hold person on Jroc!");
        for (int i = 0; i < 8; i++) RunOneStep(h);
        Assert.True(h.Solver.Active);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Finished);

        // A freedom cast on the held member releases the pause → finishes.
        h.Solver.FeedLineForTests("Xian casts freedom on Jroc!");
        RunToEnd(h);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void KeyGrabber_LeaderGrabbed_SkipsForcedGive()
    {
        using Harness h = NewHarness(leaderName: "MudPlay");
        LocateFirepit(h);
        h.Solver.TryBegin(new RoomKey(12, 2085));
        h.Solver.FeedLineForTests("You picked up golden lion key.");   // leader grabbed it
        RunToEnd(h);

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        // Leader already holds the key, so no forced consolidation was sent.
        Assert.DoesNotContain(h.SentText, t => t.StartsWith("@party give golden lion key", StringComparison.Ordinal));
    }

    // One iteration of the run loop (same dispatch as RunToEnd) — used to pump a
    // fixed number of steps while checking a stalled state.
    private static void RunOneStep(Harness h)
    {
        if (!h.Solver.Active) return;
        switch (h.Solver.PhaseName)
        {
            case "AwaitingSphinx":
                h.Solver.FeedLineForTests("With a loud grinding noise, a concealed passage opens in the ceiling!");
                break;
            case "AwaitingDoor":
                h.Solver.OnRoomObserved(new RoomObservation("Great Pyramid", AllDirs, AllDirs));
                break;
            default:
                h.Solver.FireSettleForTests();
                break;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }
}
