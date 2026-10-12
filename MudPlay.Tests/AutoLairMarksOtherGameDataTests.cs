using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Lair marks placed on the map are rooms of the game data that was active when they
// were placed. When the set changes to another they are dropped, as a loop being
// built is: saved as a setup they would land in the other set's Loops folder. The
// same set read in again keeps them, and a set of marks is never mixed.
public sealed class AutoLairMarksOtherGameDataTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-lairmarks-otherset-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string Room(int map, int room, string name, string north = "0", string south = "0") => $$"""
          { "Map Number": {{map}}, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "{{north}}", "S": "{{south}}", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // alpha: 1/1 ↔ 1/2 ↔ 1/3, a strip to mark and to start a run on. beta: the first
    // two rooms and a room alpha lacks.
    private static readonly string Alpha =
        $"[\n{Room(1, 1, "A", north: "1/2")},\n{Room(1, 2, "B", north: "1/3", south: "1/1")},\n{Room(1, 3, "C", south: "1/2")}\n]";
    private static readonly string Beta =
        $"[\n{Room(1, 1, "Other A", north: "1/2")},\n{Room(1, 2, "Other B", south: "1/1")},\n{Room(7, 7, "Other only")}\n]";

    private sealed class Harness : IDisposable
    {
        public required GameDataCache Cache { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoLairManager Lairs { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required LogService Log { get; init; }
        // Every drop the manager announced: the set the marks were of, the set active.
        public List<(string? MarkedOn, string? Active)> Dropped { get; } = new();

        // The graph on another set, with nobody telling the manager: what a set of
        // marks put back for a run after a switch stands on.
        public void GraphOn(string set)
        {
            Cache.SwitchSet(set);
            Graph.OnActiveSetChanged(set);
        }

        public void Dispose()
        {
            Lairs.Dispose();
            Timers.Dispose();
        }
    }

    private Harness OnAlpha()
    {
        foreach ((string set, string rooms) in new[] { ("alpha", Alpha), ("beta", Beta) })
        {
            Directory.CreateDirectory(Path.Combine(_root, set));
            File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), rooms);
        }
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
        Harness h = new()
        {
            Cache = cache,
            Graph = graph,
            Tracker = tracker,
            Timers = timers,
            Log = log,
            Lairs = new AutoLairManager(walker, tracker, graph, bfs, timers, log, coord),
        };
        h.Lairs.MarksOfOtherGameDataDropped += (markedOn, active) => h.Dropped.Add((markedOn, active));
        return h;
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

        Assert.True(h.Lairs.DropMarksOfOtherGameData("beta"));

        Assert.Empty(h.Lairs.Marked);
        Assert.Equal(1, changed);
        Assert.Equal(new[] { ((string?)"alpha", (string?)"beta") }, h.Dropped);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "AutoLair"
            && e.Message == "2 lair mark(s) dropped: their rooms were game data 'alpha''s and 'beta' is active now");
    }

    [Fact]
    public void TheSameGameDataReadInAgain_KeepsTheMarks()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));

        Assert.False(h.Lairs.DropMarksOfOtherGameData("alpha"));
        Assert.False(h.Lairs.DropMarksOfOtherGameData("ALPHA"));

        Assert.Single(h.Lairs.Marked);
        Assert.Empty(h.Dropped);
    }

    [Fact]
    public void NoGameData_DropsTheMarksToo()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));

        Assert.True(h.Lairs.DropMarksOfOtherGameData(null));

        Assert.Empty(h.Lairs.Marked);
    }

    [Fact]
    public void WithNothingMarked_NothingIsDropped()
    {
        using Harness h = OnAlpha();

        Assert.False(h.Lairs.DropMarksOfOtherGameData("beta"));
        Assert.Empty(h.Dropped);
    }

    // A set of marks is never mixed: a room marked under another game data than the
    // marks held starts a fresh set, and the old ones are dropped with the notice.
    [Fact]
    public void ARoomMarkedOnTopOfAnotherGameDatasMarks_DropsThemAndStartsAFreshSet()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));
        h.Lairs.Mark(new RoomKey(1, 3));
        h.GraphOn("beta");

        h.Lairs.Mark(new RoomKey(7, 7));

        Assert.Equal(new[] { new RoomKey(7, 7) }, h.Lairs.Marked);
        Assert.Equal("beta", h.Lairs.MarksGameData);
        Assert.Equal(new[] { ((string?)"alpha", (string?)"beta") }, h.Dropped);

        // Back on the first set the room it lacks is not carried there either.
        h.GraphOn("alpha");
        Assert.True(h.Lairs.DropMarksOfOtherGameData("alpha"));
        Assert.Empty(h.Lairs.Marked);
    }

    [Fact]
    public void MarkingAgainOnTheirOwnGameData_AddsToTheSet()
    {
        using Harness h = OnAlpha();
        h.Lairs.Mark(new RoomKey(1, 1));

        h.Lairs.Mark(new RoomKey(1, 3));

        Assert.Equal(2, h.Lairs.Marked.Count);
        Assert.Empty(h.Dropped);
    }

    // What an event puts back after taking over from a run: the marks with the stamp
    // they had, while that game data is still the active one.
    [Fact]
    public void MarksOfTheActiveGameData_ArePutBackWithTheirStamp()
    {
        using Harness h = OnAlpha();
        Dictionary<RoomKey, int?> marks = new() { [new RoomKey(1, 1)] = null, [new RoomKey(1, 3)] = 120 };

        Assert.True(h.Lairs.RestoreMarks(marks, "alpha"));

        Assert.Equal(2, h.Lairs.Marked.Count);
        Assert.Equal(120, h.Lairs.GetOverride(new RoomKey(1, 3)));
        Assert.Equal("alpha", h.Lairs.MarksGameData);
    }

    [Fact]
    public void MarksOfAnotherGameData_AreNotPutBack()
    {
        using Harness h = OnAlpha();
        Dictionary<RoomKey, int?> marks = new() { [new RoomKey(1, 1)] = null, [new RoomKey(1, 3)] = 120 };
        h.GraphOn("beta");

        Assert.False(h.Lairs.RestoreMarks(marks, "alpha"));

        Assert.Empty(h.Lairs.Marked);
        Assert.Contains(h.Log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "AutoLair"
            && e.Message == "2 lair mark(s) not put back: their rooms were game data 'alpha''s and 'beta' is active now");
    }
}
