using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Step-aligned translation of a MegaMUD loop onto a small hand-built map: every .mp
// step gets a row, a step that can't be followed leaves a blank row instead of
// failing the import (issue #243), the walk finds its way back by hash, and rooms set
// by hand hold.
public sealed class MpFileImporterTests : IDisposable
{
    private readonly string _root;

    public MpFileImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-mpimporter-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("Webbed Lair (complete)-17 2674", "Webbed Lair (complete)")]
    [InlineData("Ancient Crypt-1 1943",           "Ancient Crypt")]
    [InlineData("Crypt Level 1 Loop-1 1028",      "Crypt Level 1 Loop")]
    [InlineData("VU20 Loop",                      "VU20 Loop")]
    [InlineData("Crypt Level 1",                  "Crypt Level 1")]
    [InlineData("",                               "")]
    public void StripMapRoomSuffix_Cases(string input, string expected)
        => Assert.Equal(expected, MpFileImporter.StripMapRoomSuffix(input));

    // A square: 1/1 Start —N→ 1/2 North —E→ 1/4 Corner —S→ 1/3 East —W→ 1/1.
    private const string Square = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "N": "1/2", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North",
            "N": "0", "S": "1/1", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "East",
            "N": "1/4", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Corner",
            "N": "0", "S": "1/3", "E": "0", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager Graph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Square);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    private static string H(RoomGraphManager g, int room) => MegaMudHash.ComputeHashExits(g.GetRoom(new RoomKey(1, room))!);

    // Build a .mp from (room-number-or-hash, flags, action) rows; the start room is
    // the first row's.
    private static MpLoopFile Mp(params (string Hash, string Flags, string Action)[] steps)
    {
        string start = steps[0].Hash;
        string text = "[Square-1 1][Tester]\n[SQRE:Test:Start]\n"
            + $"{start}:{start}:{steps.Length}:-1:0:::\n"
            + string.Join("\n", steps.Select(s => $"{s.Hash}:{s.Flags}:{s.Action}"));
        return MpFileParser.Parse(text);
    }

    [Fact]
    public void CleanLoop_EveryStepExact_AndCloses()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = Mp((H(g, 1), "0000", "n"), (H(g, 2), "0000", "e"), (H(g, 4), "0000", "s"), (H(g, 3), "0000", "w"));
        MpTranslation t = new MpFileImporter(g).TranslateCandidates(file).Single();

        Assert.Equal(new RoomKey(1, 1), t.Anchor);
        Assert.True(t.Closes);
        Assert.Equal(0, t.Blanks);
        Assert.All(t.Rows, r => Assert.Equal(MpRowStatus.Exact, r.Status));
        Assert.Equal(new RoomKey?[] { new(1, 1), new(1, 2), new(1, 4), new(1, 3) }, t.Rows.Select(r => r.Room));
    }

    // Our map has no NE from North, and the recording's next room matches nothing,
    // so that row is left blank; the walk finds East by its hash and still closes.
    [Fact]
    public void MissingExit_LeavesABlankRow_AndResyncsByHash()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = Mp((H(g, 1), "0000", "n"), (H(g, 2), "0000", "ne"), ("ABC00000", "0000", "s"), (H(g, 3), "0000", "w"));
        MpTranslation t = new MpFileImporter(g).Translate(file, new RoomKey(1, 1));

        Assert.Equal(MpRowStatus.Blank, t.Rows[2].Status);
        Assert.Null(t.Rows[2].Room);
        Assert.Contains("no NE exit", t.Rows[2].Note);
        Assert.Equal(MpRowStatus.Resynced, t.Rows[3].Status);
        Assert.Equal(new RoomKey(1, 3), t.Rows[3].Room);
        Assert.True(t.Closes);
    }

    [Fact]
    public void RoomSetByHand_HoldsAndTheWalkCarriesOnFromIt()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = Mp((H(g, 1), "0000", "n"), (H(g, 2), "0000", "ne"), ("ABC00000", "0000", "s"), (H(g, 3), "0000", "w"));
        MpTranslation t = new MpFileImporter(g).Translate(file, new RoomKey(1, 1),
            new Dictionary<int, RoomKey> { [2] = new(1, 4) });

        Assert.Equal(MpRowStatus.UserSet, t.Rows[2].Status);
        Assert.Equal(new RoomKey(1, 3), t.Rows[3].Room);   // S from Corner
        Assert.Equal(0, t.Blanks);
        Assert.True(t.Closes);
    }

    // A command step follows our map to the room whose hash is the next step's; the
    // walker takes that passage itself, so the command isn't carried.
    [Fact]
    public void CommandStep_ThroughAKnownPassage_IsNotCarried()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = Mp((H(g, 1), "0000", "n"), (H(g, 2), "0000", "go path"), (H(g, 4), "0000", "s"), (H(g, 3), "0000", "w"));
        MpTranslation t = new MpFileImporter(g).Translate(file, new RoomKey(1, 1));

        Assert.Equal(new RoomKey(1, 4), t.Rows[2].Room);
        Assert.True(t.Rows[1].PassageKnown);
        Assert.Null(MpFileImporter.DefaultWaypoint(file.Steps[1], new RoomKey(1, 2), t.Rows[1].PassageKnown).Command);
    }

    // A command that doesn't move (a lever) stays in the room; the repeat folds into
    // one waypoint that keeps the command.
    [Fact]
    public void CommandThatStaysInTheRoom_FoldsIntoOneWaypointWithTheCommand()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = Mp((H(g, 1), "0000", "pull lever"), (H(g, 1), "0000", "n"),
            (H(g, 2), "0000", "e"), (H(g, 4), "0000", "s"), (H(g, 3), "0000", "w"));
        MpTranslation t = new MpFileImporter(g).Translate(file, new RoomKey(1, 1));
        Assert.Equal(new RoomKey(1, 1), t.Rows[1].Room);
        Assert.False(t.Rows[0].PassageKnown);

        List<LoopWaypoint> wps = MpFileImporter.Waypoints(t.Rows.Select(r =>
            (LoopWaypoint?)MpFileImporter.DefaultWaypoint(file.Steps[r.Index], r.Room!.Value, r.PassageKnown)));
        Assert.Equal(4, wps.Count);
        Assert.Equal("pull lever", wps[0].Command);
    }

    [Fact]
    public void DefaultWaypoint_CarriesTheStepOptionsWeSupport()
    {
        MpStep step = MpFileParser.ParseStep("AAAAAAAA", MpStepFlags.RestHere | MpStepFlags.DontRest | MpStepFlags.NoAttack | MpStepFlags.Dark, "sea");
        LoopWaypoint w = MpFileImporter.DefaultWaypoint(step, new RoomKey(1, 1), passageKnown: false);
        Assert.True(w.RestHereHp);
        Assert.True(w.RestHereMana);
        Assert.True(w.DoNotRest);
        Assert.True(w.DoNotAttack);
        Assert.Null(w.Command);   // searches are automatic
    }

    [Fact]
    public void Waypoints_DropBlanks_AndTheClosingRepeatOfTheStart()
    {
        List<LoopWaypoint> wps = MpFileImporter.Waypoints(new LoopWaypoint?[]
        {
            new(new RoomKey(1, 1)), null, new(new RoomKey(1, 2)), new(new RoomKey(1, 1)),
        });
        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, wps.Select(w => w.Key));
    }

    [Fact]
    public void NoRoomMatchesTheStart_NoCandidates()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = MpFileParser.Parse("[No hint][]\n[X:G:N]\nFFF00000:FFF00000:2:-1:0:::\nFFF00000:0000:n\nFFF00000:0000:s");
        Assert.Empty(new MpFileImporter(g).TranslateCandidates(file));
    }

    // The start room's exits changed since the recording, so its full hash matches
    // nothing — the label's "-map room" hint still finds it.
    [Fact]
    public void LabelHint_FindsTheStartWhenItsHashDrifted()
    {
        RoomGraphManager g = Graph();
        MpLoopFile file = MpFileParser.Parse("[Square-1 1][]\n[SQRE:G:Start]\nFFF00000:FFF00000:2:-1:0:::\nFFF00000:0000:n\nFFF00000:0000:s");
        Assert.Contains(new RoomKey(1, 1), new MpFileImporter(g).FindAnchorCandidates(file));
    }

    // No hint either: rooms matching on the name hash alone are tried.
    [Fact]
    public void NameHashAlone_FindsTheStartAsALastResort()
    {
        RoomGraphManager g = Graph();
        string nameHash = MegaMudHash.ComputeNameHash("Start");
        string drifted = nameHash + "FFFFF";
        MpLoopFile file = MpFileParser.Parse($"[No hint][]\n[X:G:N]\n{drifted}:{drifted}:2:-1:0:::\n{drifted}:0000:n\n{drifted}:0000:s");
        Assert.Equal(new[] { new RoomKey(1, 1) }, new MpFileImporter(g).FindAnchorCandidates(file));
    }

    [Theory]
    [InlineData("Wererat loop-8 910", 8, 910)]
    [InlineData("Western Road Fork- 2 61", 2, 61)]
    [InlineData("Green Dragons (FULL loop, no great)", 0, 0)]
    public void MapRoomHint_Cases(string label, int map, int room)
    {
        RoomKey? hint = MpFileImporter.MapRoomHint(label);
        if (map == 0) Assert.Null(hint);
        else Assert.Equal(new RoomKey(map, room), hint);
    }
}
