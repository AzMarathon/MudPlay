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
// and whole folders are deleted by those names. None of the three is trusted: each
// must name an entry directly inside the folder above it (PathGuard), and no read,
// write, move or delete may land outside the set's Simulator folder whatever it is
// handed. Every case here leaves a set of canary files, placed where a bad path
// would reach, exactly as they were.
//
// The BBS names carry the test's own tag: moves and deletes by BBS walk every set
// under the data root, other test classes' included. The class runs in the
// collection of the tests that draw refusals, one after another: a refusal is said
// once and remembered for a while, across the whole process.
[Collection(ScratchRoot.NamesCollection)]
public sealed class AreaRankingFilesSafetyTests : IDisposable
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];
    private readonly string _set;
    private readonly string _otherSet;
    private readonly List<string> _canaries = new();
    private readonly List<string> _said = new();
    private readonly AreaRankingFiles _files;

    private string Board => $"board-{_tag}";
    private string Tavern => $"tavern-{_tag}";
    private string Good => $"{Board}/one";

    private static readonly RankingWorld World = new(
        LoopSimulator.SimulatorVersion, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    public AreaRankingFilesSafetyTests()
    {
        ScratchRoot.Require();
        _set = $"test-safe-a-{_tag}";
        _otherSet = $"test-safe-b-{_tag}";
        _files = new AreaRankingFiles();
        PathGuard.Refused += Hear;
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

    private void Hear(string line) { lock (_said) _said.Add(line); }

    // The refusals drawn by names that carry this test's tag: the guard's are the
    // whole process's, and any other test's go by too.
    private List<string> Said { get { lock (_said) return _said.Where(l => l.Contains(_tag)).ToList(); } }

    public void Dispose()
    {
        PathGuard.Refused -= Hear;
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

    // Names that would lead somewhere other than a folder of that name: up, down a
    // level, to an absolute path, or to no name at all.
    public static TheoryData<string> Hostile => new()
    {
        "..", ".", "../..", "a/b", "/etc/x", "", " ", "   ", "./x", "x/..", "~/x", "a/", "nul\0l",
    };

    // Names nothing would be given today, and that are one path segment where the
    // file system allows them: a profile or BBS made under one before names were
    // checked is a character like any other, and keeps its rankings.
    public static TheoryData<string> OddButHarmless => new()
    {
        "name.", "a:b", "Sir: Bob", "a\\b", "\\x", "C:x", "C:\\x", "name ", "a\u0001b", "a\nb", "..\\..", "CON",
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
    public void ARefusalIsSaidThroughTheSharedGuard_OnceForEachOperationAndName()
    {
        // Each name carries this test's tag, so the lines are its own whatever any
        // other test has had refused before it.
        string bbs = $"../{_tag}";
        string profile = $"../p{_tag}";
        _files.DeleteBbs(bbs);
        _files.DeleteBbs(bbs);
        _files.Write(new SimulatorOwner(_set, $"{Board}/{profile}"), Ranking());
        _files.Write(new SimulatorOwner(_set, $"{Board}/{profile}"), Ranking());
        _files.Read(new SimulatorOwner($"../s{_tag}", Good), World);

        List<string> said = Said;
        Assert.Equal(3, said.Count);
        Assert.All(said, m => Assert.EndsWith("nothing on disk was changed.", m));
        Assert.Contains($"Deleting a BBS's saved area rankings refused: '{bbs}' can't be used as a BBS name; nothing on disk was changed.", said);
        Assert.Contains($"Saving an area ranking refused: '{profile}' can't be used as a profile name; nothing on disk was changed.", said);
        Assert.Contains($"Reading a saved area ranking refused: '../s{_tag}' can't be used as a game-data set name; nothing on disk was changed.", said);
        AssertNothingElseTouched();
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
        // A set named the way an imported one is has a file's place, made or not.
        Assert.Equal(
            Path.Combine(AppPaths.GameDataRoot, "data-Paradigm-1.9.1 (room commands)", "Simulator", Board, "one.ranking.json"),
            AreaRankingFiles.FileFor(new SimulatorOwner("data-Paradigm-1.9.1 (room commands)", Good)));
        Assert.Empty(Said);
        AssertNothingElseTouched();
    }

    [Theory]
    [MemberData(nameof(OddButHarmless))]
    public void AnOddButHarmlessProfileOrBbsName_GetsItsFile_InsideTheSimulatorFolder(string odd)
    {
        if (OperatingSystem.IsWindows()) return;   // none of these is one folder's name there
        string simulator = AppPaths.SimulatorRankingsFolder(_set);
        string oddBbs = $"{_tag}{odd}";
        foreach (var owner in new[] { new SimulatorOwner(_set, $"{Board}/{odd}"), new SimulatorOwner(_set, $"{oddBbs}/one") })
        {
            string file = Assert.IsType<string>(AreaRankingFiles.FileFor(owner));
            Assert.True(PathGuard.IsInside(file, simulator), file);
            Assert.Null(_files.Write(owner, Ranking()).Error);
            Assert.True(File.Exists(file));
            Assert.NotNull(_files.Read(owner, World).Ranking);
        }

        // It goes with the character to a plain name, and with its BBS when that goes.
        _files.MoveCharacter($"{Board}/{odd}", $"{Board}/renamed");
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_set, $"{Board}/renamed"))));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_set, $"{Board}/{odd}"))));
        _files.DeleteBbs(oddBbs);
        Assert.False(Directory.Exists(Path.Combine(simulator, oddBbs)));

        Assert.Empty(Said);
        AssertNothingElseTouched();
    }

    // ----- A link where a folder should be ---------

    // Somewhere outside the set, holding what a delete or a move gone through a link
    // would take.
    private string Victim()
    {
        string victim = Path.Combine(AppPaths.DataRoot, $"victim-{_tag}");
        Canary(Path.Combine(victim, "one.ranking.json"));
        Canary(Path.Combine(victim, "deep", "two.ranking.json"));
        Canary(Path.Combine(victim, "other.txt"));
        return victim;
    }

    [Fact]
    public void ABbsFolderThatIsALinkToElsewhere_IsNotReadWrittenMovedOrEmptied()
    {
        if (OperatingSystem.IsWindows()) return;   // making a link needs a privilege there
        string victim = Victim();
        string linked = $"linked-{_tag}";
        string link = Path.Combine(AppPaths.SimulatorRankingsFolder(_set), linked);
        Directory.CreateSymbolicLink(link, victim);
        var owner = new SimulatorOwner(_set, $"{linked}/one");

        Assert.Null(AreaRankingFiles.FileFor(owner));
        Assert.Equal((null, 0L), _files.Write(owner, Ranking()));
        Assert.Equal(default, _files.Read(owner, World));
        _files.Delete(owner);
        _files.MoveCharacter(owner.Character, $"{Board}/two");
        _files.MoveBbs(linked, Tavern);
        _files.DeleteBbs(linked);
        AssertNothingElseTouched();
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(_set, $"{Tavern}/one"))));

        // The set's rankings going takes the link, as a link, and nothing through it.
        _files.DeleteSet(_set);
        Assert.False(Directory.Exists(link));
        foreach (string canary in _canaries.Where(c => c.StartsWith(victim, StringComparison.Ordinal)))
            Assert.True(File.Exists(canary), $"gone: {canary}");
        try { Directory.Delete(victim, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a scratch folder */ }
    }

    [Fact]
    public void ASimulatorFolderThatIsALinkToElsewhere_IsLeftAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        string victim = Victim();
        string set = $"test-safe-linked-{_tag}";
        Directory.CreateDirectory(AppPaths.GameDataSetDir(set));
        Directory.CreateSymbolicLink(AppPaths.SimulatorRankingsFolder(set), victim);
        // Through the link, "deep" is a BBS's folder and its file a character's.
        var owner = new SimulatorOwner(set, "deep/two");

        try
        {
            Assert.Null(AreaRankingFiles.FileFor(owner));
            Assert.Equal((null, 0L), _files.Write(owner, Ranking()));
            Assert.Equal(default, _files.Read(owner, World));
            _files.Delete(owner);
            _files.MoveCharacter(owner.Character, null);
            _files.MoveBbs("deep", Tavern);
            _files.DeleteBbs("deep");
            Assert.Equal(0, _files.DeleteSet(set));

            foreach (string canary in _canaries.Where(c => c.StartsWith(victim, StringComparison.Ordinal)))
                Assert.True(File.Exists(canary), $"gone: {canary}");
            Assert.Equal(3, Directory.EnumerateFiles(victim, "*", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(AppPaths.GameDataSetDir(set), recursive: true);
            Directory.Delete(victim, recursive: true);
        }
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
