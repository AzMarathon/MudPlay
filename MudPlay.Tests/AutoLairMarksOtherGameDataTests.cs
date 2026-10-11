using System;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Lair marks placed on the map are rooms of the game data that was active when they
// were placed. When the set changes to another they are dropped, as a loop being
// built is: saved as a setup they would land in the other set's Loops folder. The
// same set read in again keeps them, and a run under way is left its marks.
public sealed class AutoLairMarksOtherGameDataTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-lairmarks-otherset-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3, a strip to mark and to start a run on.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoLairManager Lairs { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required LogService Log { get; init; }
        public void Dispose()
        {
            Lairs.Dispose();
            Timers.Dispose();
        }
    }

    private Harness OnAlpha()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        LogService log = new();
        return new Harness
        {
            Tracker = tracker,
            Timers = timers,
            Log = log,
            Lairs = new AutoLairManager(walker, tracker, graph, bfs, timers, log, coord),
        };
    }

    [Fact]
    public void MarksAreStampedWithTheGameDataTheyWerePlacedOn()
    {
        using Harness h = OnAlpha();

        h.Lairs.Mark(new RoomKey(1, 1));

        Assert.Equal("alpha", h.Lairs.MarksGameData);
    }

    [Fact]
    public void AnotherGameData_DropsTheMarks_AndSaysWhoseTheyWere()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3), overrideRespawnSeconds: 90);
        int changed = 0;
        h.Lairs.MarkedChanged += () => changed++;

        Assert.True(h.Lairs.DropMarksOfOtherGameData("beta", out string? markedOn));

        Assert.Equal("alpha", markedOn);
        Assert.Empty(h.Lairs.Marked);
        Assert.Equal(1, changed);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "AutoLair"
            && e.Message == "2 lair mark(s) dropped: their rooms were game data 'alpha''s and 'beta' is active now");
    }

    [Fact]
    public void TheSameGameDataReadInAgain_KeepsTheMarks()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));

        Assert.False(h.Lairs.DropMarksOfOtherGameData("alpha", out _));
        Assert.False(h.Lairs.DropMarksOfOtherGameData("ALPHA", out _));

        Assert.Single(h.Lairs.Marked);
    }

    [Fact]
    public void NoGameData_DropsTheMarksToo()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));

        Assert.True(h.Lairs.DropMarksOfOtherGameData(null, out _));

        Assert.Empty(h.Lairs.Marked);
    }

    [Fact]
    public void WithNothingMarked_NothingIsDropped()
    {
        using Harness h = OnAlpha();

        Assert.False(h.Lairs.DropMarksOfOtherGameData("beta", out _));
    }

    // A run is left to carry on as it would have; its marks still say whose they
    // are, which is what refuses saving them as a setup under the other set.
    [Fact]
    public void ARunUnderWay_KeepsItsMarks_StillStampedWithTheirOwnGameData()
    {
        using Harness h = OnAlpha();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3));
        Assert.True(h.Lairs.Start());

        Assert.False(h.Lairs.DropMarksOfOtherGameData("beta", out _));

        Assert.True(h.Lairs.IsActive);
        Assert.Equal(2, h.Lairs.Marked.Count);
        Assert.Equal("alpha", h.Lairs.MarksGameData);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "AutoLair"
            && e.Message == "2 lair mark(s) kept for the run under way: their rooms are game data 'alpha''s and 'beta' is active now");

        // Once the run is over the next change of game data drops them.
        h.Lairs.Stop();
        Assert.True(h.Lairs.DropMarksOfOtherGameData("beta", out _));
        Assert.Empty(h.Lairs.Marked);
    }
}
