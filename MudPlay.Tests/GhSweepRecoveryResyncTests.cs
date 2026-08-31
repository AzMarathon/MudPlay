using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Regression coverage for a reported-but-unreproduced failure mode: "rooms
// being missed, just walking past them after sending rm" during Roomba
// Mode's recon phase, on a gang house whose rooms all share the same
// display name (heavy Suspect/rm-resync traffic as a result). The hypothesis
// was that a recovery-driven resume (RoomTracker.SetLocated /
// EngineRecoveryGate.NoteAuthoritativePosition) might resolve a room's
// position WITHOUT re-raising the RoomTracker.StateChanged signal
// GhSweepManager's recon dispatch depends on — a MISSING dispatch, distinct
// from the misordered-dispatch races fixed elsewhere in LoopRunner this
// session. These tests exercise that exact chain end to end and both pass,
// proving the chain is sound as currently wired — SetLocated does fire a
// genuine transition for an early-registered reactor, and that reactor's
// gate correctly holds the loop through a full Suspect -> rm -> resolve
// cycle. The root cause of the field report was NOT found or reproduced;
// see GhSweepManager.OnRoomChanged's diagnostic logging (added alongside
// these tests) for what a future structured log capture needs to pin it
// down definitively.
public sealed class GhSweepRecoveryResyncTests : IDisposable
{
    private readonly string _root;

    public GhSweepRecoveryResyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-ghsweep-resync-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void SetLocated_FiresGenuineStateChanged_ForEarlyRegisteredReactor()
    {
        // ParadigmPositionResolver.OnLocationObserved calls _tracker.SetLocated(key)
        // when an `rm` reply resolves — this is the one path that must produce a
        // real RoomTransition (NewRoom != PreviousRoom) for GhSweepManager's early
        // wrapper subscriber to react to, or a resync-resolved room's recon
        // dispatch never fires.
        RoomTracker tracker = new(NewGraph());
        List<RoomTransition> seen = new();
        tracker.StateChanged += t => seen.Add(t);

        tracker.SetLocated(new RoomKey(1, 1));
        seen.Clear();
        tracker.SetLocated(new RoomKey(1, 2));

        RoomTransition t = Assert.Single(seen);
        Assert.NotNull(t.NewRoom);
        Assert.Equal(new RoomKey(1, 2), t.NewRoom!.Key);
        Assert.NotNull(t.PreviousRoom);
        Assert.Equal(new RoomKey(1, 1), t.PreviousRoom!.Key);
    }

    [Fact]
    public void ReconDispatchReactor_HoldsThroughFullSuspectRmResolveCycle()
    {
        // End-to-end reproduction of the reported sequence: a mid-step Suspect
        // classification (the ambiguous-room-name resync trigger any of
        // LoopRunner's three NoteSuspectedMismatch call sites can raise — the
        // synchronous mismatch handler, or the in-flight stall watchdog timer)
        // fires an `rm`; the reply resolves via the exact two-call sequence
        // ParadigmPositionResolver.OnLocationObserved uses
        // (tracker.SetLocated then gate.NoteAuthoritativePosition). A stand-in
        // for GhSweepManager.OnRoomChanged, registered before LoopRunner (the
        // same early-wrapper wiring AppServices uses), must see the resolved
        // room's genuine transition, hold the loop via its own gate, and let
        // exactly one move ship once that gate later clears.
        RoomGraphManager graph = NewGraph();
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        BfsMapper bfs = new(graph);

        bool reconning = false;
        List<RoomKey> dispatchedFor = new();
        tracker.StateChanged += t =>
        {
            if (!reconning) return;   // GhSweepManager.Phase is Idle before Start() — must not react yet
            if (t.NewRoom is null) return;
            if (t.PreviousRoom is { } prev && prev.Key.Equals(t.NewRoom.Key)) return;
            dispatchedFor.Add(t.NewRoom.Key);
            coord.AssertGate("TestReconGate");
        };

        EngineRecoveryGate gate = new(graph, tracker);
        List<string> resyncReasons = new();
        gate.TryResync = reason => { resyncReasons.Add(reason); return true; };

        List<Action> posted = new();
        LoopRunner runner = new(tracker, coord, graph: graph, recovery: gate, bfs: bfs,
            postToUi: posted.Add);
        List<byte[]> sent = new();
        runner.SetWireSender(sent.Add);

        tracker.SetLocated(new RoomKey(1, 1));
        reconning = true;
        Assert.True(runner.Start(new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) })));
        Assert.Single(sent);   // step 0: move toward 1/2

        // A Suspect classification fires mid-step (either trigger source —
        // NoteSuspectedMismatch's own behavior doesn't distinguish them).
        gate.NoteSuspectedMismatch("test: ambiguous room name mid-step");
        Assert.Single(resyncReasons);
        Assert.Equal(LoopState.Paused, runner.State);
        Assert.Empty(dispatchedFor);   // no genuine room change yet — nothing to dispatch

        // The `rm` reply resolves.
        tracker.SetLocated(new RoomKey(1, 2));
        gate.NoteAuthoritativePosition(new RoomKey(1, 2));

        Assert.Contains(new RoomKey(1, 2), dispatchedFor);
        Assert.True(coord.IsGateAsserted("TestReconGate"));
        Assert.Single(sent);   // must not have shipped the next move while the recon gate is up

        // Recon "finishes" and releases its gate — exactly one deferred move ships.
        coord.ClearGate("TestReconGate");
        Action[] batch = posted.ToArray();
        posted.Clear();
        foreach (Action a in batch) a();

        Assert.Equal(2, sent.Count);
    }
}
