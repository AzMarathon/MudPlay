using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// A loop's restart after a reconnect, on the wire: the game's chunks go through the
// prompt scanner first and then the terminal, as MainWindowViewModel feeds them, so
// the reconnect's prompt is read ahead of the room display that came with it. The
// restart waits for that display, and plans its first step from the room it shows.
//
//   1/3 Tower
//     |N
//   1/2 Yard
//     |N
//   1/1 Gate ─E─ 1/4 Corridor ─E─ 1/5 Corridor ─E─ 1/6 Cellar
//
// The loop goes round Gate and Yard. The two corridors look the same.
public sealed class LoopReconnectRestartRoomTests
{
    private static readonly RoomKey Gate = new(1, 1);
    private static readonly RoomKey Yard = new(1, 2);
    private static readonly RoomKey Tower = new(1, 3);
    private static readonly RoomKey Corridor = new(1, 4);

    private static readonly string Rooms = TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 1, "Gate", ("N", "1/2"), ("E", "1/4")),
        TrackerWireRig.Room(1, 2, "Yard", ("S", "1/1"), ("N", "1/3")),
        TrackerWireRig.Room(1, 3, "Tower", ("S", "1/2")),
        TrackerWireRig.Room(1, 4, "Corridor", ("W", "1/1"), ("E", "1/5")),
        TrackerWireRig.Room(1, 5, "Corridor", ("W", "1/4"), ("E", "1/6")),
        TrackerWireRig.Room(1, 6, "Cellar", ("W", "1/5")),
    });

    private static Loop GateYard() => new("gy", new[] { Gate, Yard });

    private sealed class World : IDisposable
    {
        public TrackerWireRig Rig { get; }
        public LoopRunner Runner { get; }
        private readonly List<IDisposable> _dispose = new();

        // Logged in, standing in `at`.
        public World(RoomKey at)
        {
            Rig = new TrackerWireRig(Rooms, prompt: "[HP=100/MA=50]:") { Position = at };
            Runner = new LoopRunner(Rig.Tracker, Rig.Coordinator, Rig.Prompts, Rig.Log, Rig.Graph, Rig.Gate,
                new BfsMapper(Rig.Graph), Rig.Walker, postToUi: Rig.Post);
            Runner.SetWireSender(Rig.Send);
            Rig.ShowRoom();
            Assert.Equal(at, Rig.Believed);
        }

        // A leader's party, its reform after a reconnect and the hold a dropped
        // member puts on movement, wired as AppServices wires them.
        public PartyReformCoordinator? Reform { get; private set; }

        public void LeadParty(string follower)
        {
            PartyManager party = new(Rig.Router, new PartyState(), Rig.Log);
            Reform = new PartyReformCoordinator(Rig.Router, party, log: Rig.Log);
            PartyDisconnectMovementGate hold = new(party, Rig.Coordinator, Rig.Log);
            _dispose.AddRange(new IDisposable[] { hold, Reform, party });
            PartyReformCoordinator reform = Reform;
            Runner.SetReconnectReformProbe(() => reform.PendingReform.Count > 0);
            Rig.Show($"{follower} started to follow you.");
        }

        // The link drops, in the order MainWindowViewModel's handler tells the pieces.
        public void Drop()
        {
            Reform?.NoteDisconnected();
            Runner.NotifyDisconnected();
            Rig.Tracker.NoteConnectionLost();
            Rig.DropLink();
        }

        // Back in the game: the realm shows the room `at` and its prompt, in one chunk.
        public void Reconnect(RoomKey at)
        {
            Reform?.Arm();
            Rig.Position = at;
            Rig.ShowRoom();
        }

        public int Mark() => Rig.Sent.Count;
        public List<string> SentSince(int mark) => Rig.Sent.Skip(mark).ToList();

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_dispose)) d.Dispose();
            Rig.Dispose();
        }
    }

    // Back where the link dropped: the move sent before the drop never landed, and
    // the loop takes it again from the room the reconnect shows.
    [Fact]
    public void ReconnectIntoTheSameRoom_TheLoopRestartsFromIt()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        Assert.Equal("n", w.Rig.Sent.Last());          // never answered: the link drops
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
        w.Rig.Pump(limit: 1);
        Assert.Equal(Yard, w.Rig.Believed);
    }

    // The realm puts the character back in another room. Nothing may be planned from
    // the room known before the drop: the first step is the walk back from the room
    // the reconnect shows.
    [Fact]
    public void ReconnectIntoAnotherRoom_NoStepIsPlannedFromTheOldRoom()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Tower);

        Assert.Equal(Tower, w.Rig.Believed);
        Assert.Equal("s", Assert.Single(w.SentSince(mark)));    // Tower → Yard, not Gate's "n"
        Assert.NotEqual(LoopState.Idle, w.Runner.State);
        w.Rig.Pump(limit: 1);
        Assert.Equal(Yard, w.Rig.Believed);
    }

    // The reconnect shows a room the map can't tell from another. The loop waits,
    // sending nothing, until the tracker is sure of the room again, and then
    // restarts from there.
    [Fact]
    public void ReconnectWhereTheTrackerIsUnsure_TheLoopWaitsForTheRoom()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Corridor);

        Assert.NotEqual(RoomConfidence.Confirmed, w.Rig.Tracker.State.Confidence);
        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeAwaitsRoom);
        Assert.Equal("gy", w.Runner.PendingReconnectResumeName);

        w.Rig.Show("Somebody waves.");                  // another prompt: still unsure
        Assert.Empty(w.SentSince(mark));

        w.Rig.Type("w");                                // the user steps back to the gate
        w.Rig.Pump(limit: 1);

        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal(new[] { "w", "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
        Assert.False(w.Runner.ReconnectResumeAwaitsRoom);
    }

    // The user's Stop while the restart waits for the room calls it off.
    [Fact]
    public void RestartWaitingForTheRoom_IsDroppedByAStop()
    {
        using World w = new(Gate);
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        w.Reconnect(Corridor);
        Assert.True(w.Runner.ReconnectResumeAwaitsRoom);
        int mark = w.Mark();

        w.Runner.DropHeldReconnectResume("user stop from toolbar");
        w.Rig.Type("w");
        w.Rig.Pump(limit: 1);

        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal(new[] { "w" }, w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // A loop the user starts while the restart is still owed is the run: the one set
    // aside at the drop doesn't come back over it.
    [Fact]
    public void RestartStillOwed_GivesWayToALoopTheUserStarts()
    {
        using World w = new(Gate);
        bool off = true;
        w.Runner.MasterSwitchOff = () => off;
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        w.Reconnect(Gate);
        Assert.True(w.Runner.ReconnectResumeHeldForMasterSwitch);

        Assert.True(w.Runner.Start(new Loop("mine", new[] { Gate, Yard }), userStarted: true));
        off = false;
        w.Runner.ResumeAfterMasterSwitch();

        Assert.Equal("mine", w.Runner.CurrentLoop?.Name);
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // The master switch off at the reconnect: nothing restarts, and switched back on
    // the loop restarts from the room the reconnect showed.
    [Fact]
    public void ReconnectWithTheMasterSwitchOff_RestartsFromTheShownRoomWhenItIsBackOn()
    {
        using World w = new(Gate);
        bool off = true;
        w.Runner.MasterSwitchOff = () => off;
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Tower);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Idle, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeHeldForMasterSwitch);

        off = false;
        w.Runner.ResumeAfterMasterSwitch();

        Assert.Equal("s", Assert.Single(w.SentSince(mark)));
        Assert.Null(w.Runner.PendingReconnectResumeName);
    }

    // A leader's reconnect with followers to gather (report paradigm-20260923-092317):
    // the reform reads the room display that came with the prompt, so its hold is up
    // by the time the loop restarts, and the loop takes no step until the follower
    // is back.
    [Fact]
    public void ReconnectWithAPartyReform_TheLoopRestartsBehindTheReformsHold()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        Assert.True(w.Runner.Start(GateYard()));
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Paused, w.Runner.State);
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, w.Rig.Coordinator.AssertedGates);
        Assert.False(w.Runner.ReconnectResumeHeldForReform);

        w.Rig.Show("Tank started to follow you.");

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        Assert.Equal(LoopState.Running, w.Runner.State);
    }

    // A room too dark to display gives the reform nothing to read: the loop restarts
    // behind a hold of its own until the reform has had its look.
    [Fact]
    public void ReconnectIntoADarkRoomWithAPartyReform_TheLoopIsHeldForTheReform()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        w.Runner.ResumeOnReconnect(GateYard(), "a light buy the connection cut off");
        w.Drop();
        w.Rig.DarkRooms.Add(Gate);
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Empty(w.SentSince(mark));
        Assert.Equal(LoopState.Paused, w.Runner.State);
        Assert.True(w.Runner.ReconnectResumeHeldForReform);
        Assert.Contains(MovementCoordinator.ReconnectReformGate, w.Rig.Coordinator.AssertedGates);
    }

    // A loop a light buy handed over when the connection cut it off (an errand had
    // stopped it, so nothing was running at the drop) restarts from the room the
    // reconnect shows, not the shop's.
    [Fact]
    public void ALoopHandedOverByALightBuy_RestartsFromTheShownRoom()
    {
        using World w = new(Tower);
        w.Drop();
        w.Runner.ResumeOnReconnect(GateYard(), "a light buy the connection cut off");
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal(new[] { "n" }, w.SentSince(mark));          // from the gate, not Tower's "s"
        Assert.Equal(LoopState.Running, w.Runner.State);
    }
}
