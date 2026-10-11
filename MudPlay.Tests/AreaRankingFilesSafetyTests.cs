using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A ranking file's path is built from a set name, a BBS name and a profile name,
// and whole folders are deleted by those names. None of the three is trusted: a
// name that is not one plain file name is refused, and no read, write, move or
// delete may land outside the set's Simulator folder whatever it is handed. Every
// case here leaves a set of canary files, placed where a bad path would reach,
// exactly as they were.
//
// The BBS names carry the test's own tag: moves and deletes by BBS walk every set
// under the data root, other test classes' included.
public sealed class AreaRankingFilesSafetyTests : IDisposable
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];
    private readonly string _set;
    private readonly string _otherSet;
    private readonly List<string> _canaries = new();
    private readonly LogService _log = new();
    private readonly AreaRankingFiles _files;

    private string Board => $"board-{_tag}";
    private string Tavern => $"tavern-{_tag}";
    private string Good => $"{Board}/one";

    private static readonly RankingWorld World = new(
        LoopSimulator.SimulatorVersion, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    public AreaRankingFilesSafetyTests()
    {
        _set = $"test-safe-a-{_tag}";
        _otherSet = $"test-safe-b-{_tag}";
        _files = new AreaRankingFiles(log: _log);
        foreach (string set in new[] { _set, _otherSet })
        {
            Directory.CreateDirectory(AppPaths.GameDataSetDir(set));
            // A table, a loop and a ranking that belongs: what a delete gone wrong
            // would take.
            Canary(Path.Combine(AppPaths.GameDataSetDir(set), "Rooms.json"));
            Canary(Path.Combine(AppPaths.GameDataSetLoopsFolder(set), "a.loop"));
            _files.Write(new SimulatorOwner(set, Good), Ranking());
            _canaries.Add(AreaRankingFiles.FileFor(new SimulatorOwner(set, Good))!);
        }
        // Beside the sets, above the game data altogether, and where a profile name
        // that climbs one folder would land.
        Canary(Path.Combine(AppPaths.GameDataRoot, $"canary-{_tag}.txt"));
        Canary(Path.Combine(AppPaths.DataRoot, $"canary-{_tag}.txt"));
        Canary(Path.Combine(AppPaths.GameDataRoot, $"x-{_tag}.ranking.json"));
        Canary(Path.Combine(AppPaths.GameDataSetDir(_set), "one.ranking.json"));
        Canary(Path.Combine(AppPaths.SimulatorRankingsFolder(_set), "one.ranking.json"));
    }

    private void Canary(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "canary");
        _canaries.Add(path);
    }

    public void Dispose()
    {
        foreach (string set in new[] { _set, _otherSet })
        {
            try { Directory.Delete(AppPaths.GameDataSetDir(set), recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
        foreach (string file in _canaries)
        {
            try { File.Delete(file); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private void AssertNothingElseTouched()
    {
        foreach (string canary in _canaries)
            Assert.True(File.Exists(canary), $"gone: {canary}");
        Assert.True(Directory.Exists(AppPaths.GameDataSetDir(_set)));
        Assert.True(Directory.Exists(AppPaths.GameDataSetDir(_otherSet)));
    }

    private static AreaRanking Ranking() => new(
        new[]
        {
            new AreaRank("Hills", 42, new[] { new RoomKey(1, 5), new RoomKey(1, 7) }, 9,
                new LoopSimSummary(new[] { new LoopSimRun(3600, 3600, 1, 1, 1, 1, 1, 1, 1, 1, 90, 90, null, new Dictionary<string, int>()) })),
        },
        Level: 42, Step: 1.1, Hours: 1, Runs: 1, From: new RoomKey(1, 5), Realm: "Stock", Summary: "s",
        MadeAt: DateTimeOffset.Now, CharacterLevel: 42, World: World);

    // Names that are not one plain file name. Some would climb out of the folder,
    // some name a drive or a device, some Windows would quietly alter.
    public static TheoryData<string> Hostile => new()
    {
        "..", ".", "../..", "a/b", "a\\b", "/etc/x", "\\x", "C:x", "C:\\x", "name.", "name ", "", " ", "   ",
        "a\u0001b", "a\nb", "..\\..", "./x", "x/..", "~/x", "a:b",
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void AHostileProfileName_IsRefusedByEveryOperation(string piece)
    {
        var owner = new SimulatorOwner(_set, $"{Board}/{piece}");

        Assert.Null(AreaRankingFiles.FileFor(owner));
        Assert.Equal((null, 0L), _files.Write(owner, Ranking()));
        Assert.Equal(default, _files.Read(owner, World));
        _files.Delete(owner);
        _files.MoveCharacter(owner.Character, $"{Board}/two");
        _files.MoveCharacter(Good, owner.Character);
        _files.MoveCharacter(owner.Character, null);

        AssertNothingElseTouched();
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void AHostileBbsName_IsRefusedByEveryOperation(string piece)
    {
        var owner = new SimulatorOwner(_set, piece + "/one");

        Assert.Null(AreaRankingFiles.FileFor(owner));
        Assert.Equal((null, 0L), _files.Write(owner, Ranking()));
        Assert.Equal(default, _files.Read(owner, World));
        _files.Delete(owner);
        _files.MoveCharacter(owner.Character, $"{Board}/two");
        _files.MoveCharacter(Good, owner.Character);
        _files.MoveCharacter(owner.Character, null);
        _files.MoveBbs(piece, Tavern);
        _files.MoveBbs(Board, piece);
        _files.DeleteBbs(piece);

        AssertNothingElseTouched();
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void AHostileSetName_IsRefusedByEveryOperation(string piece)
    {
        var owner = new SimulatorOwner(piece, Good);

        Assert.Null(AreaRankingFiles.FileFor(owner));
        Assert.Equal((null, 0L), _files.Write(owner, Ranking()));
        Assert.Equal(default, _files.Read(owner, World));
        _files.Delete(owner);
        Assert.Equal(0, _files.DeleteSet(piece));

        AssertNothingElseTouched();
    }

    [Fact]
    public void TheReviewersThree_AnAbsolutePathAParentAndADeleteOfDotDot()
    {
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(_set, "board//etc/x")));
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(_set, "../one")));
        Assert.Equal((null, 0L), _files.Write(new SimulatorOwner(_set, "../one"), Ranking()));
        Assert.Equal(default, _files.Read(new SimulatorOwner(_set, "../one"), World));

        _files.DeleteBbs("..");
        _files.DeleteBbs(".");
        _files.MoveBbs("..", Tavern);
        _files.MoveCharacter("../one", null);
        _files.DeleteSet("..");
        _files.DeleteSet(".");

        AssertNothingElseTouched();
    }

    [Fact]
    public void ARefusalIsLoggedAtWarn_OnceForEachName()
    {
        _files.DeleteBbs("..");
        _files.DeleteBbs("..");
        _files.Write(new SimulatorOwner(_set, "../one"), Ranking());

        string[] warned = _log.Snapshot().Where(e => e.Severity == LogSeverity.Warn).Select(e => e.Message).ToArray();
        Assert.Equal(2, warned.Length);
        Assert.All(warned, m => Assert.Contains("nothing on disk was touched", m));
        Assert.Contains(warned, m => m.Contains("delete a BBS's rankings") && m.Contains("'..'"));
        Assert.Contains(warned, m => m.Contains("save") && m.Contains("'../one'"));
    }

    [Fact]
    public void PlainNamesStillWork_SpacesDotsAndPunctuationInside()
    {
        foreach (string bbs in new[] { $"My Board {_tag}", $"board.net-{_tag}", $"b-b_b (2) {_tag}", $"é{_tag}" })
        {
            var owner = new SimulatorOwner(_set, bbs + "/Some One");
            Assert.NotNull(AreaRankingFiles.FileFor(owner));
            Assert.Null(_files.Write(owner, Ranking()).Error);
            Assert.NotNull(_files.Read(owner, World).Ranking);
        }
        Assert.True(AreaRankingFiles.IsPlainName("data-Paradigm-1.9.1 (room commands)"));
        AssertNothingElseTouched();
    }

    [Fact]
    public void WhatBelongs_IsStillDeletedAndMoved_AndOnlyThat()
    {
        _files.MoveBbs(Board, Tavern);
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_set, $"{Tavern}/one"))));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_otherSet, $"{Tavern}/one"))));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.SimulatorRankingsFolder(_set), Board)));

        _files.DeleteBbs(Tavern);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_set, $"{Tavern}/one"))));

        // The set's rankings: this character's, and the stray file in the folder.
        _files.Write(new SimulatorOwner(_set, Good), Ranking());
        Assert.Equal(2, _files.DeleteSet(_set));
        Assert.False(Directory.Exists(AppPaths.SimulatorRankingsFolder(_set)));

        // Everything that is not a ranking in a Simulator folder is where it was.
        foreach (string canary in _canaries.Where(c => !c.Contains(Path.DirectorySeparatorChar + "Simulator" + Path.DirectorySeparatorChar)))
            Assert.True(File.Exists(canary), $"gone: {canary}");
    }
}
