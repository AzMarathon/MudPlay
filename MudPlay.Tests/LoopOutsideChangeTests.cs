using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Two clients running on one game-data set share its Loops folder. "This" client is
// the one under test; "the other" is a second manager on the same folder (or a file
// written straight into it), standing in for another running MudPlay.
public sealed class LoopOutsideChangeTests : IDisposable
{
    private readonly string _setName = "test-outside-" + Guid.NewGuid().ToString("N").Substring(0, 12);
    private readonly LogService _log = new();

    public LoopOutsideChangeTests() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private string Folder => AppPaths.GameDataSetLoopsFolder(_setName);

    private LoopManager NewLoops(LogService? log = null)
    {
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager m = new(new BfsMapper(graph), graph, log);
        m.LoadAll(_setName);
        return m;
    }

    private LairManager NewLairs(LogService? log = null)
    {
        LairManager m = new(log);
        m.LoadAll(_setName);
        return m;
    }

    private static Loop NewLoop(string name, int room = 2) =>
        new(name, new[] { new LoopWaypoint(new RoomKey(1, 1)), new LoopWaypoint(new RoomKey(1, room)) });

    private static LairSetup NewSetup(string name, int room = 10) =>
        new(name, new[] { new LairMarker(1, room) });

    private string LoopPath(string name) => Path.Combine(Folder, name + LoopManager.LoopFileSuffix);

    // A quiet poll then the one that takes the change: the settled read needs two.
    private static bool PollTwice(LoopManager m) { m.TakeInOutsideChanges(); return m.TakeInOutsideChanges(); }
    private static bool PollTwice(LairManager m) { m.TakeInOutsideChanges(); return m.TakeInOutsideChanges(); }

    // ----- loops ----------------------------------------------------

    [Fact]
    public void LoopWrittenByAnotherClient_AppearsOnTheSecondPoll_NotTheFirst()
    {
        LoopManager mine = NewLoops();
        int raised = 0;
        mine.LoopsChanged += () => raised++;

        File.WriteAllText(LoopPath("Hand"), """{ "SchemaVersion": 3, "Name": "Hand", "Waypoints": [ { "Room": "1/1" }, { "Room": "1/2" } ] }""");

        Assert.False(mine.TakeInOutsideChanges());
        Assert.Null(mine.Get("Hand"));
        Assert.True(mine.TakeInOutsideChanges());
        Assert.NotNull(mine.Get("Hand"));
        Assert.Equal(1, raised);
        Assert.NotNull(mine.LastOutsideReadAt);
        Assert.False(mine.TakeInOutsideChanges());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void LoopSavedByAnotherManager_AppearsAfterTwoPolls()
    {
        LoopManager mine = NewLoops();
        LoopManager other = NewLoops();

        other.Save(NewLoop("Sewers"));

        Assert.True(PollTwice(mine));
        Assert.Equal(2, mine.Get("Sewers")!.Waypoints.Count);
    }

    [Fact]
    public void LoopDeletedByAnotherClient_GoesAfterTwoPolls()
    {
        NewLoops().Save(NewLoop("Gone"));
        LoopManager mine = NewLoops();
        Assert.NotNull(mine.Get("Gone"));

        File.Delete(LoopPath("Gone"));

        Assert.False(mine.TakeInOutsideChanges());
        Assert.NotNull(mine.Get("Gone"));
        Assert.True(mine.TakeInOutsideChanges());
        Assert.Null(mine.Get("Gone"));
    }

    [Fact]
    public void LoopEditedByAnotherClient_IsReplacedByANewObject()
    {
        NewLoops().Save(NewLoop("Edited"));
        LoopManager mine = NewLoops();
        Loop before = mine.Get("Edited")!;

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Edited")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);

        Assert.True(PollTwice(mine));
        Loop after = mine.Get("Edited")!;
        Assert.NotSame(before, after);
        Assert.Equal(3, after.Waypoints.Count);
        Assert.Equal(2, before.Waypoints.Count);
    }

    [Fact]
    public void LoopRenamedByAnotherClient_FollowsAsOneGoneAndOneNew()
    {
        NewLoops().Save(NewLoop("Old name"));
        LoopManager mine = NewLoops();

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Old name")!;
        other.Delete("Old name");
        theirs.Name = "New name";
        other.Save(theirs);

        Assert.True(PollTwice(mine));
        Assert.Null(mine.Get("Old name"));
        Assert.NotNull(mine.Get("New name"));
    }

    [Fact]
    public void LoopMovedToAFolderByAnotherClient_FollowsWithItsFolder()
    {
        NewLoops().Save(NewLoop("Mover"));
        LoopManager mine = NewLoops();
        Assert.Equal("", mine.Get("Mover")!.Folder);

        NewLoops().Move("Mover", "Sewers/Lower");

        Assert.True(PollTwice(mine));
        Assert.Equal("Sewers/Lower", mine.Get("Mover")!.Folder);
        Assert.Single(mine.Loops);
    }

    [Fact]
    public void EmptyFolderMadeByAnotherClient_RaisesTheChangeEvent()
    {
        LoopManager mine = NewLoops();
        int raised = 0;
        mine.LoopsChanged += () => raised++;

        Directory.CreateDirectory(Path.Combine(Folder, "Fresh"));

        Assert.True(PollTwice(mine));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void OwnSaveMoveAndDelete_AreNotReadBackAsOutsideChanges()
    {
        LoopManager mine = NewLoops();
        mine.Save(NewLoop("Mine"));
        mine.Move("Mine", "Deep/Down");
        mine.Save(NewLoop("Other"));
        mine.Delete("Other");
        int raised = 0;
        mine.LoopsChanged += () => raised++;

        Assert.False(PollTwice(mine));
        Assert.False(mine.TakeInOutsideChanges());
        Assert.Equal(0, raised);
        Assert.Null(mine.LastOutsideReadAt);
        Assert.Equal("Deep/Down", mine.Get("Mine")!.Folder);
    }

    [Fact]
    public void OwnSaveBesideAnotherClientsSave_StillTakesTheOthers()
    {
        LoopManager mine = NewLoops();
        LoopManager other = NewLoops();

        other.Save(NewLoop("Theirs"));
        mine.Save(NewLoop("Mine"));

        Assert.True(PollTwice(mine));
        Assert.NotNull(mine.Get("Theirs"));
        Assert.NotNull(mine.Get("Mine"));
    }

    [Fact]
    public void ABurstOfSaves_IsReadOnceSettled()
    {
        LoopManager mine = NewLoops();
        LoopManager other = NewLoops();
        int raised = 0;
        mine.LoopsChanged += () => raised++;

        other.Save(NewLoop("One"));
        mine.TakeInOutsideChanges();
        other.Save(NewLoop("Two"));
        Assert.False(mine.TakeInOutsideChanges());   // moved since the last poll: still waiting
        other.Save(NewLoop("Three"));
        Assert.False(mine.TakeInOutsideChanges());
        Assert.True(mine.TakeInOutsideChanges());

        Assert.Equal(1, raised);
        Assert.Equal(3, mine.Loops.Count);
    }

    [Fact]
    public void SettleNow_ReadsWithoutWaiting()
    {
        LoopManager mine = NewLoops();
        NewLoops().Save(NewLoop("Quick"));

        Assert.True(mine.TakeInOutsideChanges(settleNow: true));
        Assert.NotNull(mine.Get("Quick"));
    }

    [Fact]
    public async Task MovingALoop_NeverLeavesItMissingFromTheFolder()
    {
        LoopManager mine = NewLoops();
        string sub = Path.Combine(Folder, "Elsewhere");
        string current = "";
        int missing = 0;
        using CancellationTokenSource stop = new();
        // Another client looking: the root first, then the sub-folder the loop moves
        // to, so a loop written there before it leaves the root is always found.
        Task watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string name = Volatile.Read(ref current);
                if (name.Length == 0) continue;
                if (!File.Exists(LoopPath(name)) && !File.Exists(Path.Combine(sub, name + LoopManager.LoopFileSuffix)))
                    Interlocked.Increment(ref missing);
            }
        });
        for (int i = 0; i < 300; i++)
        {
            string name = "Mover" + i;
            Loop loop = NewLoop(name);
            mine.Save(loop);
            Volatile.Write(ref current, name);
            loop.Folder = "Elsewhere";
            mine.Save(loop);
        }
        stop.Cancel();
        await watcher;

        Assert.Equal(0, missing);
        Assert.Equal(300, mine.Loops.Count);
    }

    [Fact]
    public void UnreadableFile_KeepsTheCopyInMemory_AndIsLoggedOnce()
    {
        NewLoops().Save(NewLoop("Fragile"));
        LoopManager mine = NewLoops(_log);
        Loop held = mine.Get("Fragile")!;

        File.WriteAllText(LoopPath("Fragile"), "{ this is not json");

        PollTwice(mine);
        mine.TakeInOutsideChanges();
        mine.TakeInOutsideChanges();

        Assert.Same(held, mine.Get("Fragile"));
        Assert.Equal(1, _log.Snapshot().Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be read")));
        Assert.DoesNotContain(_log.Snapshot(), e => e.Message.Contains("re-read after an outside change"));
    }

    [Fact]
    public void RunningLoop_KeepsItsCopy_WhenTheFileIsEditedElsewhere()
    {
        NewLoops().Save(NewLoop("Running"));
        LoopManager mine = NewLoops(_log);
        mine.NamesInUse = () => new[] { "Running" };
        Loop running = mine.Get("Running")!;
        List<LoopWaypoint> waypointsAtStart = running.Waypoints.ToList();

        LoopManager other = NewLoops();
        Loop theirs = other.Get("Running")!;
        theirs.Waypoints.Add(new LoopWaypoint(new RoomKey(1, 3)));
        other.Save(theirs);
        PollTwice(mine);

        Assert.Equal(waypointsAtStart, running.Waypoints);
        Assert.Equal("Running", running.Name);
        Assert.NotSame(running, mine.Get("Running"));
        Assert.Contains(_log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains("loop 'Running' was edited in another client") && e.Message.Contains("copy it started with"));
        Assert.Contains(_log.Snapshot(), e => e.Message.Contains("loops re-read after an outside change: 0 added, 1 changed, 0 removed (changed: 'Running')"));
    }

    [Fact]
    public void RunningLoop_KeepsItsCopy_WhenTheFileIsDeletedElsewhere()
    {
        NewLoops().Save(NewLoop("Running"));
        LoopManager mine = NewLoops(_log);
        mine.NamesInUse = () => new[] { "Running" };
        Loop running = mine.Get("Running")!;

        File.Delete(LoopPath("Running"));
        PollTwice(mine);

        Assert.Null(mine.Get("Running"));
        Assert.Equal(2, running.Waypoints.Count);
        Assert.Equal("Running", running.Name);
        Assert.Contains(_log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains("loop 'Running' was deleted or renamed in another client") && e.Message.Contains("copy it started with"));
    }

    [Fact]
    public void LoopsOutsideAnyRun_DoNotLogTheRunNote()
    {
        NewLoops().Save(NewLoop("Idle"));
        LoopManager mine = NewLoops(_log);
        mine.NamesInUse = () => new[] { "Something else" };

        File.Delete(LoopPath("Idle"));
        PollTwice(mine);

        Assert.DoesNotContain(_log.Snapshot(), e => e.Message.Contains("copy it started with"));
    }

    [Fact]
    public void ReReadLog_NamesTheLoops_OnlyWhenFiveOrFewer()
    {
        LoopManager mine = NewLoops(_log);
        LoopManager other = NewLoops();
        for (int i = 0; i < 6; i++) other.Save(NewLoop("Bulk " + i));
        PollTwice(mine);
        other.Save(NewLoop("Lone"));
        PollTwice(mine);

        string[] lines = _log.Snapshot().Select(e => e.Message).Where(m => m.Contains("re-read after an outside change")).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("loops re-read after an outside change: 6 added, 0 changed, 0 removed", lines[0]);
        Assert.Equal("loops re-read after an outside change: 1 added, 0 changed, 0 removed (added: 'Lone')", lines[1]);
    }

    [Fact]
    public void FavouriteNamedBeforeTheLoopExists_ShowsWhenItArrivesFromAnotherClient()
    {
        LoopManager mine = NewLoops();
        HashSet<string> favourites = new(StringComparer.OrdinalIgnoreCase) { "Later" };
        mine.AttachFavorites(favourites.Contains, (n, on) => { if (on) favourites.Add(n); else favourites.Remove(n); });

        NewLoops().Save(NewLoop("Later"));
        PollTwice(mine);

        Assert.True(mine.Get("Later")!.Favorite);
    }

    [Fact]
    public async Task EditorOverwriteCheck_SeesALoopSavedByAnotherClient()
    {
        LoopManager mine = NewLoops();
        RoomGraphManager graph = new(new GameDataCache());
        LoopEditorDialogViewModel vm = new(NewLoop("Draft"), mine, graph, isNew: true) { Name = "Shared" };

        NewLoops().Save(NewLoop("Shared", room: 9));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("already exists", vm.SaveError);
        Assert.Equal(2, NewLoops().Get("Shared")!.Waypoints.Count);
        Assert.Equal(9, NewLoops().Get("Shared")!.Waypoints[1].Key.Room);
    }

    // ----- lair setups ----------------------------------------------

    [Fact]
    public void SetupWrittenByAnotherClient_AppearsOnTheSecondPoll_NotTheFirst()
    {
        LairManager mine = NewLairs();
        int raised = 0;
        mine.SetupsChanged += () => raised++;

        NewLairs().Save(NewSetup("Crypt"));

        Assert.False(mine.TakeInOutsideChanges());
        Assert.Null(mine.Get("Crypt"));
        Assert.True(mine.TakeInOutsideChanges());
        Assert.NotNull(mine.Get("Crypt"));
        Assert.Equal(1, raised);
        Assert.NotNull(mine.LastOutsideReadAt);
    }

    [Fact]
    public void SetupDeletedByAnotherClient_GoesAfterTwoPolls()
    {
        NewLairs().Save(NewSetup("Crypt"));
        LairManager mine = NewLairs();

        NewLairs().Delete("Crypt");

        Assert.True(PollTwice(mine));
        Assert.Null(mine.Get("Crypt"));
    }

    [Fact]
    public void SetupMovedAndEditedByAnotherClient_Follows()
    {
        NewLairs().Save(NewSetup("Crypt"));
        LairManager mine = NewLairs();
        LairSetup before = mine.Get("Crypt")!;

        LairManager other = NewLairs();
        other.Move("Crypt", "Undead");
        PollTwice(mine);
        Assert.Equal("Undead", mine.Get("Crypt")!.Folder);

        LairSetup theirs = other.Get("Crypt")!;
        theirs.Markers.Add(new LairMarker(1, 11));
        other.Save(theirs);
        PollTwice(mine);
        Assert.Equal(2, mine.Get("Crypt")!.Markers.Count);
        Assert.Single(before.Markers);
    }

    [Fact]
    public void OwnSetupSaveMoveAndDelete_AreNotReadBackAsOutsideChanges()
    {
        LairManager mine = NewLairs();
        mine.Save(NewSetup("Mine"));
        mine.Move("Mine", "Deep");
        mine.Save(NewSetup("Gone"));
        mine.Delete("Gone");
        int raised = 0;
        mine.SetupsChanged += () => raised++;

        Assert.False(PollTwice(mine));
        Assert.Equal(0, raised);
        Assert.Null(mine.LastOutsideReadAt);
    }

    [Fact]
    public void UnreadableSetupFile_KeepsTheCopyInMemory_AndIsLoggedOnce()
    {
        NewLairs().Save(NewSetup("Fragile"));
        LairManager mine = NewLairs(_log);
        LairSetup held = mine.Get("Fragile")!;

        File.WriteAllText(Path.Combine(Folder, "Fragile" + LairManager.LairFileSuffix), "{ this is not json");
        PollTwice(mine);
        mine.TakeInOutsideChanges();

        Assert.Same(held, mine.Get("Fragile"));
        Assert.Equal(1, _log.Snapshot().Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains("can't be read")));
    }

    [Fact]
    public void SetupReReadLog_NamesTheSetups()
    {
        LairManager mine = NewLairs(_log);
        NewLairs().Save(NewSetup("Crypt"));
        PollTwice(mine);

        Assert.Contains(_log.Snapshot(), e => e.Message == "lair setups re-read after an outside change: 1 added, 0 changed, 0 removed (added: 'Crypt')");
    }

    [Fact]
    public void LoopsAndSetupsShareTheFolder_EachFollowsItsOwnFiles()
    {
        LoopManager loops = NewLoops();
        LairManager lairs = NewLairs();

        NewLoops().Save(NewLoop("A loop"));
        NewLairs().Save(NewSetup("A setup"));
        PollTwice(loops);
        PollTwice(lairs);

        Assert.Single(loops.Loops);
        Assert.Single(lairs.Setups);
    }
}
