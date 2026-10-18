using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The Default task's start at a login, on the wire: the login's room display and
// its prompt come in one chunk, the prompt read first as MainWindowViewModel feeds
// it. The task's loop is planned from the room the login shows.
//
//   1/3 Tower
//     |N
//   1/2 Yard
//     |N
//   1/1 Gate ─E─ 1/4 Corridor ─E─ 1/5 Corridor ─E─ 1/6 Cellar
//
// The two corridors look the same.
public sealed class DefaultTaskLoginRoomTests : IDisposable
{
    private static readonly RoomKey Gate = new(1, 1);
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

    // The task's loop is read from the set's Loops folder under the app's own data
    // root; a name of its own keeps it from every other test's.
    private readonly string _loopSet = "dtlogin-" + Path.GetRandomFileName().Replace(".", "");
    private readonly List<IDisposable> _dispose = new();

    public DefaultTaskLoginRoomTests() => ScratchRoot.Require();

    public void Dispose()
    {
        foreach (IDisposable d in Enumerable.Reverse(_dispose)) d.Dispose();
        try { Directory.Delete(AppPaths.GameDataSetDir(_loopSet), recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private sealed record World(TrackerWireRig Rig, DefaultTaskRunner Task, LoopRunner Runner, List<string> Notices,
        ManualTimers Ticks, Action<bool> SetSwitchOff);

    // The character's Default task is the loop Gate–Yard (or `task`); the map last
    // knew it at Gate (located false: nowhere). paradigm: a Paradigm realm, whose `rm`
    // the recovery gate's one-shot locate asks. switchOff: the master switch is off.
    private World Login(InitialTask task = InitialTask.BeginLoop, bool paradigm = false, bool located = true,
        bool switchOff = false)
    {
        TrackerWireRig rig = new(Rooms, prompt: "[HP=100/MA=50]:", paradigm: paradigm) { Position = Gate };
        bool off = switchOff;
        _dispose.Add(rig);
        ManualTimers ticks = new();
        BfsMapper bfs = new(rig.Graph);
        LoopRunner runner = new(rig.Tracker, rig.Coordinator, rig.Prompts, rig.Log, rig.Graph, rig.Gate, bfs, rig.Walker,
            postToUi: rig.Post, schedule: ticks.Arm);
        runner.SetWireSender(rig.Send);
        ParadigmPositionResolver resolver = new(rig.Router, rig.Tracker, rig.Gate, rig.Cache, rig.Log, schedule: ticks.Arm);
        resolver.SetWireSender(rig.Send);
        rig.Gate.TryResyncOnce = resolver.RequestResyncOnce;
        _dispose.Add(resolver);
        LoopManager loops = new(bfs, rig.Graph);
        loops.LoadAll(_loopSet);
        loops.Save(new Loop("gy", new[] { Gate, new RoomKey(1, 2) }));
        LairTimerStore timers = new(rig.Cache, rig.Graph, rig.Tracker);
        AutoLairManager lair = new(rig.Walker, rig.Tracker, rig.Graph, bfs, timers);
        PartyState partyState = new();
        PartyManager party = new(rig.Router, partyState);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        profile.Current!.Settings = new Dictionary<string, JsonElement>
        {
            ["General"] = JsonSerializer.SerializeToElement(
                new GeneralSettings { DefaultTask = task, DefaultLoopName = "gy" }),
        };
        DefaultTaskRunner entry = new(rig.Prompts, rig.Tracker, profile, loops, new LairManager(), runner, lair,
            partyState, party, rig.Log, schedule: ticks.Arm, recovery: rig.Gate, postToUi: rig.Post)
        {
            BlockedByMasterSwitch = _ => off,
        };
        List<string> notices = new();
        entry.SetStartNotice(notices.Add);
        _dispose.AddRange(new IDisposable[] { entry, lair, timers, party });
        if (located) rig.Tracker.SetLocated(Gate);
        entry.NotifyConnected();
        return new World(rig, entry, runner, notices, ticks, value => off = value);
    }

    // Put back in another room at the login: the task's loop is planned from there.
    [Fact]
    public void LoginIntoAnotherRoom_TheTaskStartsFromTheShownRoom()
    {
        World w = Login();

        w.Rig.Position = Tower;
        w.Rig.ShowRoom();

        Assert.Equal(Tower, w.Rig.Believed);
        Assert.Equal("gy", w.Runner.CurrentLoop?.Name);
        Assert.Equal(new[] { "s" }, w.Rig.Sent);                 // Tower → Yard, not Gate's "n"
    }

    // A login into a room the map can't tell, with nothing to ask: the task starts
    // from the room believed, and the terminal says so.
    [Fact]
    public void LoginWhereTheTrackerIsUnsure_TheTaskStartsFromTheBelievedRoom()
    {
        World w = Login();

        w.Rig.Position = Corridor;
        w.Rig.ShowRoom();

        Assert.Equal("gy", w.Runner.CurrentLoop?.Name);
        Assert.Equal(new[] { "n" }, w.Rig.Sent);
        Assert.Contains(w.Notices, n => n.StartsWith("[The default task started from 1/1 after the login", StringComparison.Ordinal));
    }

    // "Do nothing" has no room to wait for: nothing is asked of the game for it, and
    // the terminal says nothing.
    [Fact]
    public void DoNothing_ALoginTheMapCantPlace_SendsNothingAndSaysNothing()
    {
        World w = Login(InitialTask.DoNothing, paradigm: true);

        w.Rig.Position = Corridor;
        w.Rig.ShowRoom();
        w.Rig.Show("Somebody waves.");

        Assert.Empty(w.Rig.Sent);
        Assert.Empty(w.Notices);
        Assert.False(w.Task.AwaitsRoom);
    }

    // With the master switch off the task is skipped, and the game isn't asked where
    // we are for it.
    [Fact]
    public void MasterSwitchOff_ALoginTheMapCantPlace_AsksTheGameNothing()
    {
        World w = Login(paradigm: true, switchOff: true);

        w.Rig.Position = Corridor;
        w.Rig.ShowRoom();
        w.Rig.Show("Somebody waves.");

        Assert.Empty(w.Rig.Sent);
        Assert.Empty(w.Notices);
        Assert.Null(w.Runner.CurrentLoop);
    }

    // With no room known at all the task waits for one, with no time limit and no
    // notice, as it always did, and starts once the map knows where it is.
    [Fact]
    public void LoginWithNoRoomKnownAtAll_TheTaskWaitsForOne()
    {
        World w = Login(located: false);

        w.Rig.Position = Corridor;
        w.Rig.ShowRoom();
        w.Ticks.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(w.Rig.Tracker.State.CurrentRoom);
        Assert.Empty(w.Rig.Sent);
        Assert.Empty(w.Notices);
        Assert.True(w.Task.AwaitsRoom);

        w.Rig.Type("w");                                          // the user steps out to the gate
        w.Rig.Pump(limit: 1);

        Assert.Equal(Gate, w.Rig.Believed);
        Assert.Equal("gy", w.Runner.CurrentLoop?.Name);
        Assert.Equal(new[] { "w", "n" }, w.Rig.Sent);
    }

    // The switch off at the login: the task is skipped for this connection, not kept.
    // Switched back on, the next prompts start nothing.
    [Fact]
    public void MasterSwitchOffAtTheLogin_ThenBackOn_TheTaskStaysSkipped()
    {
        World w = Login(switchOff: true);

        w.Rig.Position = Tower;
        w.Rig.ShowRoom();
        w.SetSwitchOff(false);
        w.Rig.Show("Somebody waves.");
        w.Rig.Show("Somebody waves again.");

        Assert.Null(w.Runner.CurrentLoop);
        Assert.Empty(w.Rig.Sent);
    }
}
