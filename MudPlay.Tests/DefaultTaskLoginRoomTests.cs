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

    private sealed record World(TrackerWireRig Rig, DefaultTaskRunner Task, LoopRunner Runner, List<string> Notices);

    // The character's Default task is the loop Gate–Yard; the map last knew it at Gate.
    private World Login()
    {
        TrackerWireRig rig = new(Rooms, prompt: "[HP=100/MA=50]:") { Position = Gate };
        _dispose.Add(rig);
        ManualTimers ticks = new();
        BfsMapper bfs = new(rig.Graph);
        LoopRunner runner = new(rig.Tracker, rig.Coordinator, rig.Prompts, rig.Log, rig.Graph, rig.Gate, bfs, rig.Walker,
            postToUi: rig.Post, schedule: ticks.Arm);
        runner.SetWireSender(rig.Send);
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
                new GeneralSettings { DefaultTask = InitialTask.BeginLoop, DefaultLoopName = "gy" }),
        };
        DefaultTaskRunner task = new(rig.Prompts, rig.Tracker, profile, loops, new LairManager(), runner, lair,
            partyState, party, rig.Log, schedule: ticks.Arm, recovery: rig.Gate, postToUi: rig.Post);
        List<string> notices = new();
        task.SetStartNotice(notices.Add);
        _dispose.AddRange(new IDisposable[] { task, lair, timers, party });
        rig.Tracker.SetLocated(Gate);
        task.NotifyConnected();
        return new World(rig, task, runner, notices);
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
}
