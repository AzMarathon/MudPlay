using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Area rankings on disk, and the store that keeps them there: a ranking written is
// read back whole by a client started later; a file is never trusted (another
// world, another format, another owner, damage, age all drop and delete it); the
// files follow a character through a rename and go with a deleted one; and nothing
// is read until a pairing is shown.
//
// Sets are throwaway folders under the data root (AppPaths can't be pointed
// elsewhere), removed in Dispose.
public sealed class AreaRankingFilesTests : IDisposable
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];
    private readonly List<string> _sets = new();

    private string NewSet(string name)
    {
        string set = $"test-rank-{name}-{_tag}";
        Directory.CreateDirectory(AppPaths.GameDataSetDir(set));
        _sets.Add(set);
        return set;
    }

    public void Dispose()
    {
        foreach (string set in _sets)
        {
            try
            {
                string dir = AppPaths.GameDataSetDir(set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // BBS names carry the test's own tag: a move or delete by character or BBS walks
    // every set under the data root, other test classes' included.
    private string Board => $"board-{_tag}";
    private string Tavern => $"tavern-{_tag}";
    private string One => $"{Board}/one";
    private string Two => $"{Board}/two";
    private string Renamed => $"{Board}/renamed";
    private string OtherRenamed => $"other-{_tag}/renamed";
    private string ElsewhereOne => $"elsewhere-{_tag}/one";

    private static readonly RankingWorld World = new(
        LoopSimulator.SimulatorVersion, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    private static readonly RankingCharacter Character = new(RankingCharacter.Order.ToDictionary(g => g, g => "h-" + g));

    private static AreaRanking Ranking(string area = "Hills", DateTimeOffset? madeAt = null) => new(
        new[]
        {
            new AreaRank(area, 42, new[] { new RoomKey(1, 5), new RoomKey(12, 3401), new RoomKey(1, 7) }, 17,
                new LoopSimSummary(
                    new[]
                    {
                        new LoopSimRun(3600, 3600, 512_345, 310, 12, 900.5, 1800.25, 300, 0, 599.25, 31, 8, null,
                            new Dictionary<string, int> { ["bolt"] = 40, ["a"] = 280 }, Flees: 2, HungUpAtSeconds: null, DamageTaken: 9001),
                        new LoopSimRun(3600, 1200.5, 90_000, 44, 3, 200, 700, 100, 50, 150.5, 0, 2, 1200.5,
                            new Dictionary<string, int>(), Flees: 0, HungUpAtSeconds: 1100.25, DamageTaken: 4000),
                    },
                    new[] { new ExpBossStat("the warden", 1234.5, 6) })),
            new AreaRank("My loop", 42, new[] { new RoomKey(2, 2), new RoomKey(2, 3) }, 9,
                new LoopSimSummary(new[] { new LoopSimRun(3600, 3600, 1, 1, 1, 1, 1, 1, 1, 1, 90, 90, null, new Dictionary<string, int>()) }),
                IsLoop: true, Live: new[] { new LiveLoopRecord("My loop", 41, 3, 5.5, 2_000_000, 900, 1) }),
        },
        Level: 42, Step: 1.15, Hours: 1.5, Runs: 2, From: new RoomKey(1, 5), Realm: "Paradigm",
        Summary: "L42 from 1/5: 1 safe option(s)", MadeAt: madeAt ?? new DateTimeOffset(2026, 6, 18, 14, 32, 0, TimeSpan.FromHours(2)),
        CharacterLevel: 41, World: World, Character: Character);

    private static AreaRankingFiles Files(DateTimeOffset? now = null) =>
        new(() => now ?? new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.FromHours(2)));

    // ----- The files ---------

    [Fact]
    public void ARankingWrittenIsReadBackWhole()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        AreaRanking made = Ranking();

        (string? error, long bytes) = Files().Write(owner, made);
        AreaRankingFiles.ReadResult read = Files().Read(owner, World);

        Assert.Null(error);
        Assert.True(bytes > 0);
        Assert.Null(read.Dropped);
        AreaRanking back = Assert.IsType<AreaRanking>(read.Ranking);
        Assert.True(back.FromDisk);
        Assert.Equal((made.Level, made.Step, made.Hours, made.Runs, made.From, made.Realm, made.Summary, made.MadeAt, made.CharacterLevel),
            (back.Level, back.Step, back.Hours, back.Runs, back.From, back.Realm, back.Summary, back.MadeAt, back.CharacterLevel));
        Assert.Null(World.DiffersFrom(back.World!));
        Assert.Empty(back.Character!.ChangedSince(Character));
        Assert.Equal(made.Rows.Count, back.Rows.Count);
        foreach ((AreaRank was, AreaRank now) in made.Rows.Zip(back.Rows))
        {
            Assert.Equal((was.Area, was.Level, was.LapRooms, was.IsLoop), (now.Area, now.Level, now.LapRooms, now.IsLoop));
            Assert.Equal(was.Tour, now.Tour);
            Assert.Equal(was.Result.Bosses ?? Array.Empty<ExpBossStat>(), now.Result.Bosses ?? Array.Empty<ExpBossStat>());
            Assert.Equal(was.Live ?? Array.Empty<LiveLoopRecord>(), now.Live ?? Array.Empty<LiveLoopRecord>());
            foreach ((LoopSimRun a, LoopSimRun b) in was.Result.Runs.Zip(now.Result.Runs))
            {
                // A record compares its Casts by which dictionary it is, so that is
                // set aside and compared by what it holds on the next line.
                var none = new Dictionary<string, int>();
                Assert.Equal(a with { Casts = none }, b with { Casts = none });
                Assert.Equal(a.Casts.OrderBy(c => c.Key), b.Casts.OrderBy(c => c.Key));
            }
            // What the table shows comes out the same.
            Assert.Equal(was.Label, now.Label);
        }
    }

    [Fact]
    public void TheLoopsARankingRankedAreReadBackWithIt_LookedUpByNameWhateverItsCapitals()
    {
        string set = NewSet("loops");
        var owner = new SimulatorOwner(set, One);
        AreaRankingFiles files = Files();
        files.Write(owner, Ranking() with { Loops = new Dictionary<string, string> { ["Crypt run"] = "abc", ["Marsh run"] = "def" } });

        AreaRanking read = Assert.IsType<AreaRanking>(files.Read(owner, World).Ranking);
        Assert.Equal(2, read.Loops!.Count);
        Assert.Equal("abc", read.Loops["crypt RUN"]);

        // One made before loops were recorded has none, which is not the same as no loops.
        files.Write(owner, Ranking());
        Assert.Null(files.Read(owner, World).Ranking!.Loops);
    }

    [Fact]
    public void TheFileSitsInItsSetsFolderUnderItsBbs_AndNoneIsKeptForNoCharacterOrNoSet()
    {
        string set = NewSet("a");
        Files().Write(new SimulatorOwner(set, One), Ranking());

        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetDir(set), "Simulator", Board, "one.ranking.json")));
        // Not among the set's own files, where tables are listed and counted.
        Assert.Empty(Directory.EnumerateFiles(AppPaths.GameDataSetDir(set)));
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(set, null)));
        Assert.Null(AreaRankingFiles.FileFor(new SimulatorOwner(null, One)));
        Assert.Equal((null, 0L), Files().Write(new SimulatorOwner(set, null), Ranking()));
    }

    [Fact]
    public void NothingIsWrittenToASetWhoseFolderIsGone_OrForARankingWithNoRecordOfItsWorld()
    {
        var gone = new SimulatorOwner($"test-rank-gone-{_tag}", One);
        var owner = new SimulatorOwner(NewSet("a"), One);

        Assert.Equal((null, 0L), Files().Write(gone, Ranking()));
        Assert.False(Directory.Exists(AppPaths.GameDataSetDir(gone.Set!)));
        Assert.Equal((null, 0L), Files().Write(owner, Ranking() with { World = null }));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    public static TheoryData<string, RankingWorld, string> OtherWorlds => new()
    {
        { "simulator", World with { SimulatorVersion = World.SimulatorVersion + 1 }, "simulator version" },
        { "table", World with { Tables = World.Tables.ToDictionary(t => t.Key, t => t.Key == "Monsters" ? "11:2" : t.Value) }, "Monsters table has changed" },
        { "seed", World with { Seed = "another" }, "monster seed" },
    };

    [Theory]
    [MemberData(nameof(OtherWorlds))]
    public void ARankingMadeInAnotherWorldIsDroppedAndItsFileDeleted(string what, RankingWorld now, string why)
    {
        var owner = new SimulatorOwner(NewSet(what), One);
        Files().Write(owner, Ranking());

        AreaRankingFiles.ReadResult read = Files().Read(owner, now);

        Assert.Null(read.Ranking);
        Assert.Contains(why, read.Dropped);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void AFileForAnotherOwnerOrOfAnotherFormatOrTooOld_IsDropped()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        string path = AreaRankingFiles.FileFor(owner)!;

        // Another character's file copied into this one's place, by hand.
        Files().Write(new SimulatorOwner(set, Two), Ranking());
        File.Copy(AreaRankingFiles.FileFor(new SimulatorOwner(set, Two))!, path);
        Assert.Contains("it says it is for", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // A file from a client with a later format.
        Files().Write(owner, Ranking());
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Format\":1", "\"Format\":2"));
        Assert.Contains("file format 2", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // Made more than the keeping time ago.
        Files().Write(owner, Ranking());
        var later = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero) + AreaRankingFiles.MaxAge + TimeSpan.FromDays(2);
        Assert.Contains("days ago", Files(later).Read(owner, World).Dropped);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AFileThatIsNotARankingIsDroppedNotTrusted(string contents)
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        string path = AreaRankingFiles.FileFor(owner)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);

        AreaRankingFiles.ReadResult read = Files().Read(owner, World);

        Assert.Null(read.Ranking);
        Assert.NotNull(read.Dropped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ATruncatedFile_AndOneWhoseRowsAreNotRows_AreDropped()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        string path = AreaRankingFiles.FileFor(owner)!;
        Files().Write(owner, Ranking());
        string whole = File.ReadAllText(path);

        File.WriteAllText(path, whole[..(whole.Length / 2)]);
        Assert.Contains("could not be read", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, whole.Replace("12/3401", "12/x"));
        Assert.Contains("not a ranking", Files().Read(owner, World).Dropped);
        Assert.False(File.Exists(path));

        // No file at all is not a drop.
        Assert.Equal(default, Files().Read(owner, World));
    }

    [Fact]
    public void ACharactersFilesGoWithItOnEverySet_AndAreReadBackAsItsOwnUnderTheNewName()
    {
        string a = NewSet("a"), b = NewSet("b");
        AreaRankingFiles files = Files();
        files.Write(new SimulatorOwner(a, One), Ranking("Hills"));
        files.Write(new SimulatorOwner(b, One), Ranking("Marsh"));
        files.Write(new SimulatorOwner(a, Two), Ranking("Fen"));
        // Kept under the new name by whoever had it before.
        files.Write(new SimulatorOwner(b, OtherRenamed), Ranking("Dunes"));
        files.Write(new SimulatorOwner(a, OtherRenamed), Ranking("Moor"));

        files.MoveCharacter(One, OtherRenamed);

        Assert.Equal("Hills", files.Read(new SimulatorOwner(a, OtherRenamed), World).Ranking?.Rows[0].Area);
        Assert.Equal("Marsh", files.Read(new SimulatorOwner(b, OtherRenamed), World).Ranking?.Rows[0].Area);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, One), World));
        Assert.Equal(default, files.Read(new SimulatorOwner(b, One), World));
        Assert.Equal("Fen", files.Read(new SimulatorOwner(a, Two), World).Ranking?.Rows[0].Area);

        // A deleted profile's go with it.
        files.MoveCharacter(Two, null);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, Two), World));
    }

    [Fact]
    public void ARenamedBbsTakesEveryCharactersFiles_AndARemovedOneLeavesNone()
    {
        string a = NewSet("a"), b = NewSet("b");
        AreaRankingFiles files = Files();
        files.Write(new SimulatorOwner(a, One), Ranking("Hills"));
        files.Write(new SimulatorOwner(b, Two), Ranking("Marsh"));
        files.Write(new SimulatorOwner(a, ElsewhereOne), Ranking("Fen"));
        // The loaded character has told of itself first, and is there already.
        files.MoveCharacter(One, $"{Tavern}/one");

        files.MoveBbs(Board, Tavern);

        Assert.Equal("Hills", files.Read(new SimulatorOwner(a, $"{Tavern}/one"), World).Ranking?.Rows[0].Area);
        Assert.Equal("Marsh", files.Read(new SimulatorOwner(b, $"{Tavern}/two"), World).Ranking?.Rows[0].Area);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.SimulatorRankingsFolder(a), Board)));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.SimulatorRankingsFolder(b), Board)));

        files.DeleteBbs(Tavern);
        Assert.Equal(default, files.Read(new SimulatorOwner(a, $"{Tavern}/one"), World));
        Assert.Equal(default, files.Read(new SimulatorOwner(b, $"{Tavern}/two"), World));
        Assert.Equal("Fen", files.Read(new SimulatorOwner(a, ElsewhereOne), World).Ranking?.Rows[0].Area);
    }

    [Fact]
    public void ACopyOfASetsEditsAndLoops_CarriesNoRankings_AndDeletingTheSetTakesThem()
    {
        string source = NewSet("src"), dest = NewSet("dst");
        Files().Write(new SimulatorOwner(source, One), Ranking());
        File.WriteAllText(AppPaths.MessagesFile(source), "m");
        Directory.CreateDirectory(AppPaths.GameDataSetLoopsFolder(source));
        File.WriteAllText(Path.Combine(AppPaths.GameDataSetLoopsFolder(source), "a.loop"), "x");
        var manager = new GameDataSetManager(new GameDataCache(), _ => { }, _ => { });

        Assert.True(manager.Copy(source, dest, GameDataSetPart.Loops | GameDataSetPart.Messages | GameDataSetPart.RecordOverrides).Ok);
        Assert.True(manager.Move(source, dest, GameDataSetPart.Loops | GameDataSetPart.Messages).Ok);

        Assert.False(Directory.Exists(AppPaths.SimulatorRankingsFolder(dest)));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(source, One))));

        Assert.True(manager.DeleteSet(source).Ok);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(source, One))));
    }

    // ----- The store over them ---------

    private sealed class Client
    {
        public List<Action> Posted { get; } = new();
        public LogService Log { get; } = new();
        public AreaRankingStore Store { get; }

        public Client(AreaRankingFiles? files = null) =>
            Store = new AreaRankingStore(files ?? Files(), act => { lock (Posted) Posted.Add(act); }, Log);

        // Let the file work asked for finish, and what it handed back run.
        public void Settle()
        {
            for (int round = 0; round < 10; round++)
            {
                Store.Idle.GetAwaiter().GetResult();
                Action[] batch;
                lock (Posted) { batch = Posted.ToArray(); Posted.Clear(); }
                if (batch.Length == 0) return;
                foreach (Action act in batch) act();
            }
        }

        public AreaRanking? Load(SimulatorOwner owner, RankingWorld? world = null)
        {
            AreaRanking? got = null;
            Store.Load(owner, world ?? World, r => got = r);
            Settle();
            return got;
        }

        public string[] Said(LogSeverity severity) =>
            Log.Snapshot().Where(e => e.Severity == severity).Select(e => e.Message).ToArray();
    }

    [Fact]
    public void ARankingKeptIsThereForAClientStartedLater_WhichReadsNothingUntilAsked()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking());
        first.Settle();
        Assert.Contains(first.Said(LogSeverity.Info), m => m.StartsWith("saved the area ranking for"));

        var later = new Client();
        later.Settle();
        Assert.Equal(0, later.Store.Count);
        Assert.Empty(later.Store.DiskNotes);
        Assert.Null(later.Store.Get(owner));

        AreaRanking? back = later.Load(owner);

        Assert.Equal("Hills", back?.Rows[0].Area);
        Assert.True(back!.FromDisk);
        // Held from here on: asking again reads nothing.
        Assert.Same(back, later.Store.Get(owner));
    }

    [Fact]
    public void ADroppedFileIsSaidWhy_AndHandsBackNothing()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking());
        first.Settle();

        var later = new Client();
        Assert.Null(later.Load(owner, World with { Seed = "another" }));

        Assert.Contains(later.Said(LogSeverity.Info), m => m.Contains("was dropped: the monster seed"));
        Assert.Contains(later.Store.DiskNotes, n => n.Contains("was dropped"));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void RankingAgainAndReplacedGameData_DeleteTheFile_ButBeingLetGoFromMemoryDoesNot()
    {
        string a = NewSet("a"), b = NewSet("b");
        var client = new Client();
        var onA = new SimulatorOwner(a, One);
        client.Store.Keep(onA, Ranking("Hills"));
        client.Store.Keep(new SimulatorOwner(b, One), Ranking("Marsh"));
        client.Store.Keep(new SimulatorOwner(b, Two), Ranking("Fen"));
        client.Settle();

        // More rankings than memory holds, on sets with no folder (so no files).
        for (int i = 0; i < AreaRankingStore.MaxKept; i++)
            client.Store.Keep(new SimulatorOwner($"test-rank-none-{i}-{_tag}", One), Ranking());
        client.Settle();
        Assert.Null(client.Store.Get(onA));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(onA)));
        // Shown again, it is read back.
        Assert.Equal("Hills", client.Load(onA)?.Rows[0].Area);

        // A re-rank starts.
        client.Store.Forget(onA);
        client.Settle();
        Assert.False(File.Exists(AreaRankingFiles.FileFor(onA)));

        // The set's data is replaced: every character's go.
        client.Store.ForgetSet(b);
        client.Settle();
        Assert.False(Directory.Exists(AppPaths.SimulatorRankingsFolder(b)));
        Assert.Contains(client.Said(LogSeverity.Info), m => m.StartsWith("deleted 2 saved area ranking(s)"));
    }

    [Fact]
    public void ARankingMadeWhileAFileIsBeingReadStands()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        var later = new Client();
        AreaRanking? listed = Ranking("unset");
        later.Store.Load(owner, World, r => listed = r);
        AreaRanking fresh = Ranking("Marsh");
        later.Store.Keep(owner, fresh);
        later.Settle();

        Assert.Equal("unset", listed?.Rows[0].Area);
        Assert.Same(fresh, later.Store.Get(owner));
    }

    // A read is still out when the ranking it is reading is forgotten. The delete is
    // queued behind it, so the read finds the file; what it found must not come back.
    [Fact]
    public void ARankingForgottenWhileItIsBeingRead_IsNotPutBack()
    {
        var owner = new SimulatorOwner(NewSet("a"), One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        // A re-rank starts before the read of the earlier session's file is back.
        var later = new Client();
        var handed = new List<AreaRanking?>();
        later.Store.Load(owner, World, handed.Add);
        later.Store.Forget(owner);
        later.Settle();

        Assert.DoesNotContain(handed, r => r is not null);
        Assert.Null(later.Store.Get(owner));
        Assert.Equal(0, later.Store.Count);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void ARankingWhoseSetIsReplacedWhileItIsBeingRead_IsNotPutBack()
    {
        string set = NewSet("a");
        var owner = new SimulatorOwner(set, One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Settle();

        foreach (Action<AreaRankingStore> replaced in new Action<AreaRankingStore>[]
                 {
                     s => s.ForgetSet(set),
                     s => { s.NoteActiveSet(set); s.NoteActiveSet(set); },
                     s => s.ForgetCharacter(One),
                     s => s.ForgetBbs(Board),
                 })
        {
            first.Store.Keep(owner, Ranking("Hills"));
            first.Settle();
            var later = new Client();
            var handed = new List<AreaRanking?>();
            later.Store.Load(owner, World, handed.Add);
            replaced(later.Store);
            later.Settle();

            Assert.DoesNotContain(handed, r => r is not null);
            Assert.Null(later.Store.Get(owner));
            Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
        }
    }

    [Fact]
    public void AReadThatComesBackAfterSomethingElseWasForgotten_IsAskedAgain_AndStillListed()
    {
        string set = NewSet("a"), other = NewSet("b");
        var owner = new SimulatorOwner(set, One);
        var first = new Client();
        first.Store.Keep(owner, Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(other, One), Ranking("Marsh"));
        first.Settle();

        var later = new Client();
        var handed = new List<AreaRanking?>();
        later.Store.Load(owner, World, handed.Add);
        later.Store.ForgetSet(other);
        later.Settle();

        Assert.Equal("Hills", Assert.Single(handed)?.Rows[0].Area);
        Assert.NotNull(later.Store.Get(owner));
    }

    [Fact]
    public void AWriteThatFailsDoesNotThrow_IsSaidOnce_AndTheRankingIsStillHeld()
    {
        string set = NewSet("a");
        var client = new Client();
        // A file where the rankings folder would be: nothing can be written under it.
        File.WriteAllText(AppPaths.SimulatorRankingsFolder(set), "in the way");

        AreaRanking made = Ranking();
        client.Store.Keep(new SimulatorOwner(set, One), made);
        client.Store.Keep(new SimulatorOwner(set, Two), Ranking());
        client.Settle();

        Assert.Same(made, client.Store.Get(new SimulatorOwner(set, One)));
        Assert.Single(client.Said(LogSeverity.Warn), m => m.Contains("could not be saved"));
        Assert.Equal(2, client.Store.DiskNotes.Count(n => n.Contains("could not be saved")));
    }

    [Fact]
    public void ARenameThatOnlyChangesCapitals_MovesTheFile_SoALaterClientFindsIt()
    {
        string set = NewSet("a");
        string capitals = $"{Board}/ONE";
        var first = new Client();
        AreaRanking made = Ranking("Hills");
        first.Store.Keep(new SimulatorOwner(set, One), made);
        first.Settle();

        // Nothing to carry in memory, which matches names without case.
        Assert.Equal(0, first.Store.RenameCharacter(One, capitals));
        first.Settle();
        Assert.Same(made, first.Store.Get(new SimulatorOwner(set, capitals)));

        // On disk the file is under the new spelling, to the letter.
        string kept = AreaRankingFiles.FileFor(new SimulatorOwner(set, capitals))!;
        Assert.Contains(Path.GetFileName(kept), Directory.EnumerateFiles(Path.GetDirectoryName(kept)!).Select(Path.GetFileName));
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, capitals))?.Rows[0].Area);

        // Said twice, or with nothing under the old spelling, it loses nothing.
        first.Store.RenameCharacter(One, capitals);
        first.Settle();
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, capitals))?.Rows[0].Area);
    }

    [Fact]
    public void ABbsRenameThatOnlyChangesCapitals_KeepsEveryRankingUnderIt()
    {
        string set = NewSet("a");
        string capitals = Board.ToUpperInvariant();
        Assert.NotEqual(Board, capitals);
        var first = new Client();
        first.Store.Keep(new SimulatorOwner(set, One), Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(set, Two), Ranking("Fen"));
        first.Settle();
        string oldFolder = Path.GetDirectoryName(AreaRankingFiles.FileFor(new SimulatorOwner(set, One))!)!;
        // Not a ranking: where capitals are told apart it stays behind, and is not
        // taken with the folder.
        File.WriteAllText(Path.Combine(oldFolder, "notes.txt"), "kept");

        var later = new Client();
        later.Store.RenameBbs(Board, capitals);
        later.Settle();

        string one = One[(Board.Length + 1)..], two = Two[(Board.Length + 1)..];
        Assert.Equal("Hills", new Client().Load(new SimulatorOwner(set, $"{capitals}/{one}"))?.Rows[0].Area);
        Assert.Equal("Fen", new Client().Load(new SimulatorOwner(set, $"{capitals}/{two}"))?.Rows[0].Area);
        Assert.True(File.Exists(Path.Combine(oldFolder, "notes.txt")));
    }

    [Fact]
    public void ACharacterRenamedMovedOrDeleted_TakesItsFilesOrLosesThem_WhetherOrNotHeldInMemory()
    {
        string a = NewSet("a"), b = NewSet("b");
        var first = new Client();
        first.Store.Keep(new SimulatorOwner(a, One), Ranking("Hills"));
        first.Store.Keep(new SimulatorOwner(b, One), Ranking("Marsh"));
        first.Store.Keep(new SimulatorOwner(a, Two), Ranking("Fen"));
        first.Settle();

        // A later client holds none of them in memory: these are profiles not loaded.
        var later = new Client();
        later.Store.RenameCharacter(One, Renamed);
        later.Store.ForgetCharacter(Two);
        later.Settle();

        Assert.Equal("Hills", later.Load(new SimulatorOwner(a, Renamed))?.Rows[0].Area);
        Assert.Equal("Marsh", later.Load(new SimulatorOwner(b, Renamed))?.Rows[0].Area);
        Assert.Null(later.Load(new SimulatorOwner(a, One)));
        Assert.Null(later.Load(new SimulatorOwner(a, Two)));

        // Its BBS renamed, then removed: held in memory or not.
        later.Store.RenameBbs(Board, Tavern);
        later.Settle();
        Assert.Equal("Hills", later.Store.Get(new SimulatorOwner(a, $"{Tavern}/renamed"))?.Rows[0].Area);
        Assert.Null(later.Store.Get(new SimulatorOwner(a, Renamed)));
        Assert.True(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(b, $"{Tavern}/renamed"))));

        later.Store.ForgetBbs(Tavern);
        later.Settle();
        Assert.Equal(0, later.Store.Count);
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(a, $"{Tavern}/renamed"))));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(new SimulatorOwner(b, $"{Tavern}/renamed"))));
    }

    // ----- Stamped with the tables this client read, over the real cache ---------

    // A running client on a set: the cache, with the set's tables read as a load
    // reads them, and the watch the app puts over it.
    private sealed class Running
    {
        public GameDataCache Cache { get; } = new(AppPaths.GameDataRoot);
        public RankingWorldWatch Watch { get; }

        public Running(string set, params string[] read)
        {
            Cache.SwitchSet(set);
            foreach (string table in read) Assert.NotNull(Cache.GetTable(table));
            Watch = RankingWorldWatch.Over(Cache);
            Watch.Observe(Cache.ActiveLoad);
        }

        // What a ranking finishing now is stamped with.
        public RankingWorld? MadeIn(out string? why) => Watch.NowForSaving(Cache.ActiveLoad, "seed", out why);
        public RankingWorld? MadeIn() => MadeIn(out _);
        // What a saved ranking is compared with.
        public RankingWorld? World() => Watch.Now(Cache.ActiveLoad, "seed", out _);
    }

    private static readonly string[] Tables = RankingWorld.TableNames.Where(t => t != "Lairs").ToArray();

    private string NewSetWithTables(string name = "tables")
    {
        string set = NewSet(name);
        foreach (string table in Tables) File.WriteAllText(TableFile(set, table), """[{"Number":1}]""");
        return set;
    }

    private static string TableFile(string set, string table) => Path.Combine(AppPaths.GameDataSetDir(set), table + ".json");

    // The file as another client's import leaves it: other rows, another length.
    private static void ImportedElsewhere(string set, string table) =>
        File.WriteAllText(TableFile(set, table), """[{"Number":1},{"Number":2}]""");

    private static string StampOf(string file) =>
        RankingWorldWatch.Stamp(new FileInfo(file).Length, new FileInfo(file).LastWriteTimeUtc);

    [Fact]
    public void ARankingFinishedAfterATableWasReplacedOnDisk_IsStampedWithTheFileThatWasRead()
    {
        string set = NewSetWithTables();
        var owner = new SimulatorOwner(set, One);
        string rooms = TableFile(set, "Rooms");
        string asLoaded = StampOf(rooms);
        var running = new Running(set, "Rooms", "Monsters");

        // Another client imports the set; this one is not told and reloads nothing.
        ImportedElsewhere(set, "Rooms");
        Assert.NotEqual(asLoaded, StampOf(rooms));

        RankingWorld madeIn = Assert.IsType<RankingWorld>(running.MadeIn(out string? why));
        Assert.Null(why);
        Assert.Equal(asLoaded, madeIn.Tables["Rooms"]);
        Assert.Equal(RankingWorldWatch.NoTable, madeIn.Tables["Lairs"]);
        // Every other table was read to be stamped: none is vouched for off the disk.
        Assert.All(Tables, t => Assert.NotNull(running.Cache.FileStampAsRead(t)));

        var client = new Client();
        client.Store.Keep(owner, Ranking() with { World = madeIn });
        client.Settle();
        Assert.True(File.Exists(AreaRankingFiles.FileFor(owner)));

        // The same client lists it again as its own: it is still on those tables.
        var sameSession = new Client();
        Assert.NotNull(sameSession.Load(owner, running.World()));

        // A client started afterwards reads the imported Rooms, and drops it.
        client.Store.Keep(owner, Ranking() with { World = madeIn });
        client.Settle();
        var restarted = new Running(set, "Rooms", "Monsters");
        var later = new Client();
        Assert.Null(later.Load(owner, restarted.World()));
        Assert.Contains(later.Said(LogSeverity.Info), m => m.Contains("was dropped") && m.Contains("Rooms table has changed"));
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
    }

    [Fact]
    public void OnceTheChangedFileIsReadAgainInTheSameLoad_NoRankingIsSaved_UntilTheSetIsReadInAgain()
    {
        string set = NewSetWithTables();
        var owner = new SimulatorOwner(set, One);
        var running = new Running(set, Tables);
        GameDataCache.Load load = running.Cache.ActiveLoad;
        ImportedElsewhere(set, "Rooms");

        // The idle sweep drops the table and the next lookup reads the new file: the
        // room map built at load is of the old one, the rows from here on of the new.
        running.Cache.EvictIdle(TimeSpan.Zero);
        Assert.False(running.Cache.IsTableLoaded("Rooms"));
        Assert.NotNull(running.Cache.GetTable("Rooms"));
        Assert.Equal(load, running.Cache.ActiveLoad);

        Assert.Null(running.MadeIn(out string? why));
        Assert.Equal("Rooms was read again from a file that had changed since the set was loaded", why);
        Assert.Null(running.World());
        Assert.Equal("Rooms was read again from a file that had changed since the set was loaded", running.Watch.WhyUnfit);

        // A ranking made now carries no world, and one with none is never written.
        var client = new Client();
        client.Store.Keep(owner, Ranking() with { World = running.MadeIn() });
        client.Settle();
        Assert.False(File.Exists(AreaRankingFiles.FileFor(owner)));
        Assert.NotNull(client.Store.Get(owner));

        // Read in again, it is one version once more, and that is the imported one.
        running.Cache.ReloadActiveSet();
        Assert.NotEqual(load, running.Cache.ActiveLoad);
        foreach (string table in Tables) running.Cache.GetTable(table);
        RankingWorld reloaded = Assert.IsType<RankingWorld>(running.MadeIn());
        Assert.Equal(StampOf(TableFile(set, "Rooms")), reloaded.Tables["Rooms"]);
        Assert.Null(running.Watch.WhyUnfit);
    }

    [Fact]
    public void ATableThatWasNeverRead_IsReadToBeStamped_AndOneThatWillNotReadStopsTheSave()
    {
        string set = NewSetWithTables();
        var running = new Running(set, "Rooms");

        // Not read yet and readable: read now, and stamped by that read.
        Assert.Null(running.Cache.FileStampAsRead("Items"));
        RankingWorld madeIn = Assert.IsType<RankingWorld>(running.MadeIn());
        Assert.Equal(StampOf(TableFile(set, "Items")), madeIn.Tables["Items"]);
        Assert.NotNull(running.Cache.FileStampAsRead("Items"));

        // Not read yet and not a table: no stamp is taken off the disk for it.
        string other = NewSetWithTables("broken");
        File.WriteAllText(TableFile(other, "Items"), "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        var broken = new Running(other, "Rooms");
        Assert.Null(broken.MadeIn(out string? why));
        Assert.Equal("Items could not be read", why);
        // To compare a saved ranking with, nothing is read and the file stands in.
        Assert.NotNull(broken.World());
    }

    [Fact]
    public void ATableReadAndThenNotReadableAgain_LeavesTheLoadUnfit()
    {
        string set = NewSetWithTables();
        var running = new Running(set, Tables);
        Assert.NotNull(running.MadeIn());

        // Dropped, its file replaced by one that will not parse, and asked for again.
        running.Cache.EvictIdle(TimeSpan.Zero);
        File.WriteAllText(TableFile(set, "Spells"), "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        Assert.Null(running.Cache.GetTable("Spells"));
        Assert.Null(running.Cache.FileStampAsRead("Spells"));

        Assert.Null(running.MadeIn(out string? why));
        Assert.Equal("Spells could not be read again (the file is gone or would not read)", why);
        Assert.Null(running.World());

        // A table whose file goes missing after it was read is the same.
        var gone = new Running(NewSetWithTables("gone"), Tables);
        gone.Cache.EvictIdle(TimeSpan.Zero);
        File.Delete(TableFile(gone.Cache.ActiveSet!, "Races"));
        Assert.Null(gone.Cache.GetTable("Races"));
        Assert.Null(gone.MadeIn(out why));
        Assert.StartsWith("Races could not be read again", why);
    }
}
