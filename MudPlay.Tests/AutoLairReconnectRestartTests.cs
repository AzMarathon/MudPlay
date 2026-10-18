using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// An Auto-Lair run cut off by a dropped link, restarted after the reconnect, on the
// wire: the game's chunks go through the prompt scanner first and then the terminal,
// as MainWindowViewModel feeds them, so the reconnect's prompt is read ahead of the
// room display that came with it.
//
//   1/3 Den (lair)             1/7 Passage   1/8 Passage
//     |N                         |N            |N
//   1/2 Yard                     |             |
//     |N                         |             |
//   1/1 Gate ─E─ 1/4 Hall ───────┘ ─E─ 1/5 Bend ┘ ─E─ 1/6 Cave (lair)
//
// The two passages look the same. Den is waited for from Yard, Cave from Bend.
public sealed class AutoLairReconnectRestartTests
{
    private static readonly RoomKey Gate = new(1, 1);
    private static readonly RoomKey Yard = new(1, 2);
    private static readonly RoomKey Den = new(1, 3);
    private static readonly RoomKey Hall = new(1, 4);
    private static readonly RoomKey Bend = new(1, 5);
    private static readonly RoomKey Cave = new(1, 6);
    private static readonly RoomKey Passage = new(1, 7);

    private const int RespawnSeconds = 600;
    private const string Setup = "Two dens";

    private static readonly string Rooms = TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 1, "Gate", ("N", "1/2"), ("E", "1/4")),
        TrackerWireRig.Room(1, 2, "Yard", ("S", "1/1"), ("N", "1/3")),
        LairRoom(TrackerWireRig.Room(1, 3, "Den", ("S", "1/2")), "1/3"),
        TrackerWireRig.Room(1, 4, "Hall", ("W", "1/1"), ("E", "1/5"), ("N", "1/7")),
        TrackerWireRig.Room(1, 5, "Bend", ("W", "1/4"), ("E", "1/6"), ("N", "1/8")),
        LairRoom(TrackerWireRig.Room(1, 6, "Cave", ("W", "1/5")), "1/6"),
        TrackerWireRig.Room(1, 7, "Passage", ("S", "1/4")),
        TrackerWireRig.Room(1, 8, "Passage", ("S", "1/5")),
    });

    // A room of the rig's with a lair group in it, as the game data tags one.
    private static string LairRoom(string room, string key) =>
        room.Replace("\"Lair\":\"\"", $"\"Lair\":\"[1-1-1][1]Group(lair): {key}\"");

    private sealed class World : IDisposable
    {
        public TrackerWireRig Rig { get; }
        public LairTimerStore Timers { get; }
        public ManualTimers Ticks { get; } = new();
        public AutoLairManager Lair { get; }
        private readonly List<IDisposable> _dispose = new();

        // Logged in, standing in `at`, with Den and Cave marked.
        public World(RoomKey at)
        {
            Rig = new TrackerWireRig(Rooms, prompt: "[HP=100/MA=50]:") { Position = at };
            Timers = new LairTimerStore(Rig.Cache, Rig.Graph, Rig.Tracker);
            Lair = new AutoLairManager(Rig.Walker, Rig.Tracker, Rig.Graph, new BfsMapper(Rig.Graph), Timers, Rig.Log,
                Rig.Coordinator, schedule: Ticks.Arm, promptScanner: Rig.Prompts, postToUi: Rig.Post);
            Rig.ShowRoom();
            Assert.Equal(at, Rig.Believed);
            Lair.Mark(Den, RespawnSeconds);
            Lair.Mark(Cave, RespawnSeconds);
        }

        public PartyReformCoordinator? Reform { get; private set; }

        // A leader's party, its reform after a reconnect and the hold a dropped
        // member puts on movement, wired as AppServices wires them.
        public void LeadParty(string follower)
        {
            PartyManager party = new(Rig.Router, new PartyState(), Rig.Log);
            Reform = new PartyReformCoordinator(Rig.Router, party, log: Rig.Log);
            PartyDisconnectMovementGate hold = new(party, Rig.Coordinator, Rig.Log, schedule: Ticks.Arm);
            _dispose.AddRange(new IDisposable[] { hold, Reform, party });
            PartyReformCoordinator reform = Reform;
            Lair.SetReconnectReformProbe(() => reform.PendingReform.Count > 0);
            Rig.Show($"{follower} started to follow you.");
        }

        public void Start() => Assert.True(Lair.Start(Setup));

        // The link drops, in the order MainWindowViewModel's handler tells the pieces.
        public void Drop()
        {
            Reform?.NoteDisconnected();
            Lair.NotifyDisconnected();
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
            Lair.Dispose();
            Timers.Dispose();
            Rig.Dispose();
        }
    }

    // A drop mid-run stops the run and sets it aside; the reconnect restarts it from
    // the room it shows, with nothing planned from the room before the drop.
    [Fact]
    public void DropMidRun_ReconnectElsewhere_RestartsFromTheShownRoom()
    {
        using World w = new(Gate);
        w.Start();
        Assert.Equal(Yard, w.Lair.CurrentWaitRoom);              // Den's wait room, one step north
        Assert.Equal("n", w.Rig.Sent.Last());

        w.Drop();
        Assert.False(w.Lair.IsActive);
        Assert.Equal($"Auto-Lair '{Setup}'", w.Lair.PendingReconnectResume);
        int mark = w.Mark();

        w.Reconnect(Bend);

        Assert.True(w.Lair.IsActive);
        Assert.Null(w.Lair.PendingReconnectResume);
        Assert.Equal(Cave, w.Lair.CurrentTarget);                 // Cave is waited for from here
        Assert.DoesNotContain("n", w.SentSince(mark));
        Assert.Equal(Setup, w.Lair.LastRunLairName);
    }

    // A run an errand had stopped (a light buy) is handed over with the map sure of
    // the room it dropped in; the reconnect puts the character elsewhere, and the
    // run plans from there, not from the room before the drop.
    [Fact]
    public void ARunHandedOverByALightBuy_RestartsFromTheShownRoom()
    {
        using World w = new(Yard);
        w.Start();
        w.Lair.Stop("a light buy", willResume: true);
        w.Drop();
        w.Lair.ResumeOnReconnect("a light buy the connection cut off");
        int mark = w.Mark();

        w.Reconnect(Bend);

        Assert.True(w.Lair.IsActive);
        Assert.Equal(Cave, w.Lair.CurrentTarget);                 // from Yard it would be Den
        Assert.DoesNotContain("n", w.SentSince(mark));
    }

    // Back in the same room, the run takes up the same leg again.
    [Fact]
    public void DropMidRun_ReconnectInTheSameRoom_RestartsFromIt()
    {
        using World w = new(Gate);
        w.Start();
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.True(w.Lair.IsActive);
        Assert.Equal(new[] { "n" }, w.SentSince(mark));
        w.Rig.Pump(limit: 1);
        Assert.Equal(Yard, w.Rig.Believed);
    }

    // A lair entered before the drop keeps its timer through the restart: the lair
    // clock runs on wall time, and the run is the same one.
    [Fact]
    public void LairTimersOfTheRun_SurviveTheRestart()
    {
        using World w = new(Yard);
        w.Start();
        Assert.Equal(Den, w.Lair.CurrentTarget);
        w.Ticks.RunAll();                                         // the entry is due: step in
        w.Rig.Pump(limit: 1);
        Assert.Equal(Den, w.Rig.Believed);
        DateTimeOffset? denReady = w.Timers.NextReadyAt(Den, RespawnSeconds);
        Assert.NotNull(denReady);

        w.Drop();
        w.Reconnect(Yard);

        Assert.True(w.Lair.IsActive);
        Assert.Equal(denReady, w.Timers.NextReadyAt(Den, RespawnSeconds));
        Assert.Equal(Cave, w.Lair.CurrentTarget);                 // Den isn't due again yet
    }

    // The reconnect shows a room the map can't tell from another: nothing restarts
    // until the tracker is sure of the room, and a Stop meanwhile calls it off.
    [Fact]
    public void ReconnectWhereTheTrackerIsUnsure_TheRunWaitsForTheRoom()
    {
        using World w = new(Gate);
        w.Start();
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Passage);

        Assert.NotEqual(RoomConfidence.Confirmed, w.Rig.Tracker.State.Confidence);
        Assert.False(w.Lair.IsActive);
        Assert.True(w.Lair.ReconnectResumeAwaitsRoom);
        Assert.Empty(w.SentSince(mark));

        w.Rig.Type("s");                                          // the user steps out to the hall
        w.Rig.Pump(limit: 1);

        Assert.Equal(Hall, w.Rig.Believed);
        Assert.True(w.Lair.IsActive);
        Assert.False(w.Lair.ReconnectResumeAwaitsRoom);
    }

    [Fact]
    public void RestartWaitingForTheRoom_IsDroppedByAStop()
    {
        using World w = new(Gate);
        w.Start();
        w.Drop();
        w.Reconnect(Passage);
        Assert.True(w.Lair.ReconnectResumeAwaitsRoom);

        w.Lair.DropHeldReconnectResume("user stop from toolbar");
        w.Rig.Type("s");
        w.Rig.Pump(limit: 1);

        Assert.Equal(Hall, w.Rig.Believed);
        Assert.False(w.Lair.IsActive);
        Assert.Null(w.Lair.PendingReconnectResume);
    }

    // With the master switch off the run waits for it, and restarts from the room
    // the reconnect showed when it is back on.
    [Fact]
    public void ReconnectWithTheMasterSwitchOff_RestartsWhenItIsBackOn()
    {
        using World w = new(Gate);
        bool off = true;
        w.Lair.MasterSwitchOff = () => off;
        w.Start();
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Bend);

        Assert.False(w.Lair.IsActive);
        Assert.True(w.Lair.ReconnectResumeHeldForMasterSwitch);
        Assert.Empty(w.SentSince(mark));

        off = false;
        w.Lair.ResumeAfterMasterSwitch();

        Assert.True(w.Lair.IsActive);
        Assert.Equal(Cave, w.Lair.CurrentTarget);
    }

    // A death found out at the login: the run set aside at the drop is not restarted.
    [Fact]
    public void ADeathFoundOutAtTheLogin_DropsTheRunSetAside()
    {
        using World w = new(Gate);
        w.Rig.Tracker.Hydrate(new MudPlay.Models.Profile.CharacterProfile());
        w.Rig.Tracker.SetLocated(Gate);
        w.Start();
        w.Drop();

        w.Rig.Tracker.NoteUnwitnessedDeath(new MudPlay.Game.Recovery.UnwitnessedDeath(
            null, DateTimeOffset.UtcNow, 6, "Killed by the hang-up penalty.", null, null, null));
        w.Reconnect(Gate);

        Assert.False(w.Lair.IsActive);
        Assert.Null(w.Lair.PendingReconnectResume);
    }

    // A run the user had paused comes back paused, under the user gate, and takes no
    // step until Resume.
    [Fact]
    public void ARunPausedByTheUser_ComesBackPaused()
    {
        using World w = new(Gate);
        w.Start();
        w.Lair.Pause();
        w.Drop();
        Assert.DoesNotContain(MovementCoordinator.UserGate, w.Rig.Coordinator.AssertedGates);
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.True(w.Lair.IsActive);
        Assert.True(w.Lair.IsPaused);
        Assert.Contains(MovementCoordinator.UserGate, w.Rig.Coordinator.AssertedGates);
        Assert.Empty(w.SentSince(mark));

        w.Lair.Resume();
        Assert.Equal(new[] { "n" }, w.SentSince(mark));
    }

    // A leader's reconnect: the reform reads the display that came with the prompt,
    // so its hold is up when the run restarts, and no step goes out until the
    // follower is back.
    [Fact]
    public void ReconnectWithAPartyReform_TheRunRestartsBehindTheReformsHold()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        w.Start();
        w.Drop();
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.True(w.Lair.IsActive);
        Assert.Empty(w.SentSince(mark));
        Assert.Contains(MovementCoordinator.MemberDisconnectGate, w.Rig.Coordinator.AssertedGates);
        Assert.False(w.Lair.ReconnectResumeHeldForReform);

        w.Rig.Show("Tank started to follow you.");

        Assert.Equal(new[] { "n" }, w.SentSince(mark));
    }

    // A room too dark to show the reform anything: the run restarts behind the
    // reform hold of its own.
    [Fact]
    public void ReconnectIntoADarkRoomWithAPartyReform_TheRunIsHeldForTheReform()
    {
        using World w = new(Gate);
        w.LeadParty("Tank");
        w.Start();
        w.Lair.Stop("a light buy", willResume: true);
        w.Lair.ResumeOnReconnect("a light buy the connection cut off");
        w.Drop();
        w.Rig.DarkRooms.Add(Gate);
        int mark = w.Mark();

        w.Reconnect(Gate);

        Assert.True(w.Lair.IsActive);
        Assert.True(w.Lair.ReconnectResumeHeldForReform);
        Assert.Contains(MovementCoordinator.ReconnectReformGate, w.Rig.Coordinator.AssertedGates);
        Assert.Empty(w.SentSince(mark));
    }
}
