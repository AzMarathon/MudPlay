using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Profile;
using Xunit;

namespace MudPlay.Tests;

// Where a BBS, realm, profile or set name comes in, and what each place does with
// one that can't be used: the stores' own way of failing, the naming dialogs, the
// names read back from disk and from the command line, and the profiles that were
// made under a name before names were checked.
[Collection(ScratchRoot.NamesCollection)]
public sealed class NameEntryTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "")[..8];
    private readonly ProfileService _profiles = new();
    private readonly BbsProfileStore _bbs = new();
    private readonly List<string> _said = new();

    private string Bbs => $"ne{_tag}";
    private string Other => $"ne{_tag}-other";
    private string Set => $"neset{_tag}";
    private string ProfilesDir => Path.Combine(AppPaths.BbsDir, Bbs, "profiles");

    public NameEntryTests()
    {
        ScratchRoot.Require();
        _bbs.Save(new BbsProfile { Name = Bbs, Host = "h" });
        _bbs.Save(new BbsProfile { Name = Other, Host = "h" });
        _profiles.CreateProfile(Bbs, "kept");
        Directory.CreateDirectory(Path.Combine(AppPaths.GameDataRoot, Set));
        PathGuard.Refused += Hear;
    }

    public void Dispose()
    {
        PathGuard.Refused -= Hear;
        foreach (string folder in Directory.EnumerateDirectories(AppPaths.BbsDir, $"*{_tag}*")
                     .Concat(Directory.EnumerateDirectories(AppPaths.GameDataRoot, $"*{_tag}*")))
        {
            if (!ScratchRoot.Holds(folder)) continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    private void Hear(string line) { lock (_said) _said.Add(line); }

    // The refusals this test's own names drew: other tests' lines go by too.
    private List<string> Said { get { lock (_said) return _said.Where(l => l.Contains(_tag)).ToList(); } }

    // ----- How each operation says no ---------

    [Fact]
    public void TheOperationsThatThrewForABlankName_ThrowTheSameForOneThatCannotBeUsed()
    {
        string bad = $"../x{_tag}";
        _profiles.Load(Bbs, "kept");

        Assert.Throws<ArgumentException>(() => _profiles.Load(Bbs, bad));
        Assert.Throws<ArgumentException>(() => _profiles.Load(bad, "kept"));
        Assert.Throws<ArgumentException>(() => _profiles.SaveAs(Bbs, bad));
        Assert.Throws<ArgumentException>(() => _profiles.SaveAs(bad, "named"));
        Assert.Throws<ArgumentException>(() => _profiles.CreateProfile(Bbs, bad));
        Assert.Throws<ArgumentException>(() => _profiles.CreateProfile(bad, "named"));
        Assert.Throws<ArgumentException>(() => _profiles.CopyProfile(Bbs, "kept", bad));
        Assert.Throws<ArgumentException>(() => _profiles.CopyProfile(Bbs, bad, "copy"));
        Assert.Throws<ArgumentException>(() => _profiles.MoveProfile(Bbs, "kept", Bbs, bad));
        Assert.Throws<ArgumentException>(() => _profiles.MoveProfile(Bbs, "kept", bad, "kept"));
        Assert.Throws<ArgumentException>(() => _profiles.MoveProfile(Bbs, bad, Bbs, "moved"));
        Assert.Throws<ArgumentException>(() => _profiles.ReHome(Bbs, bad));
        Assert.Throws<ArgumentException>(() => _profiles.ReHome(bad));
        Assert.Throws<ArgumentException>(() => _bbs.Save(new BbsProfile { Name = bad, Host = "h" }));

        // The loaded profile is the one it was, under the name it had.
        Assert.Equal("kept", _profiles.CurrentProfileName);
        Assert.Equal(Bbs, _profiles.CurrentBbsName);
    }

    [Fact]
    public void TheOperationsThatReturnedForABlankName_ReturnTheSame_AndSayWhy()
    {
        string bad = $"../x{_tag}";

        _profiles.DeleteProfile(Bbs, bad);
        _profiles.DeleteProfile(bad, "kept");
        _profiles.AssignRealm(new ProfileRef(Bbs, bad), "realm");
        _profiles.RenameBbs(Bbs, bad);
        _profiles.RenameRealm(bad, "old", "new");
        _bbs.Delete(bad);
        Assert.False(_profiles.Exists(Bbs, bad));
        Assert.False(_profiles.Exists(bad, "kept"));
        Assert.Null(_profiles.RealmOf(new ProfileRef(Bbs, bad)));
        Assert.Null(_bbs.Get(bad));
        Assert.False(_bbs.Exists(bad));
        Assert.False(_bbs.Rename(Bbs, bad));
        Assert.False(_bbs.Rename(bad, $"renamed{_tag}"));
        Assert.True(_bbs.Exists(Bbs));

        Assert.Contains(Said, l => l.StartsWith("Deleting a profile refused: ") && l.Contains($"'{bad}'") && l.Contains("profile name"));
        Assert.Contains(Said, l => l.StartsWith("Deleting a BBS refused: ") && l.Contains("BBS name"));
        Assert.Contains(Said, l => l.StartsWith("Renaming a BBS refused: "));
        // Asked again, the same refusal is not said again.
        int said = Said.Count;
        _profiles.DeleteProfile(Bbs, bad);
        Assert.Equal(said, Said.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task ASetThatCannotBeNamed_IsNotDeletedCopiedImportedIntoOrSwitchedTo()
    {
        string bad = $"../x{_tag}";
        var cache = new GameDataCache(AppPaths.GameDataRoot);
        var sets = new GameDataSetManager(cache, _ => { }, _ => { });

        Assert.False(sets.DeleteSet(bad).Ok);
        Assert.False(sets.DeleteSet("..").Ok);
        Assert.False(sets.Copy(bad, Set, GameDataSetPart.Loops).Ok);
        Assert.False(sets.Move(Set, bad, GameDataSetPart.Loops).Ok);
        Assert.Equal(0, sets.FileCount(bad, GameDataSetPart.Loops));

        cache.SwitchSet(bad);
        Assert.Null(cache.ActiveSet);
        cache.SwitchSet(Set);
        Assert.Equal(Set, cache.ActiveSet);
        // A plain name with nothing imported under it yet is still switched to, as before.
        cache.SwitchSet($"notyet{_tag}");
        Assert.Equal($"notyet{_tag}", cache.ActiveSet);

        string file = Path.Combine(AppPaths.GameDataRoot, Set, "x.mdb");
        File.WriteAllText(file, "x");
        MdbImportResult result = await new MdbImporter().ImportAsync(file, bad);
        Assert.False(result.Success);
        Assert.Contains("can't be used as a game-data set's name", result.Message);
        Assert.True(Directory.Exists(Path.Combine(AppPaths.GameDataRoot, Set)));
        Assert.Contains(Said, l => l.StartsWith("Deleting a game-data set refused: "));
        Assert.Contains(Said, l => l.StartsWith("Switching the game-data set refused: "));
        Assert.Contains(Said, l => l.StartsWith("Importing a database refused: "));
    }

    // ----- The naming dialogs ---------

    public static IEnumerable<object[]> Typed() =>
        PathGuardTests.NeverPlain.Concat(PathGuardTests.Plain).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Typed))]
    public void TheNameDialogRefusesExactlyWhatTheGuardRefuses(string typed)
    {
        var dialog = new ProfileNameInputDialogViewModel(typed, _ => false);
        string name = dialog.NormalizedName;   // what Save hands on: the text without the spaces around it

        Assert.Equal(name.Length > 0 && PathGuard.IsPlainName(name), dialog.CanSave);
        Assert.Equal(name.Length > 0 && !PathGuard.IsPlainName(name), dialog.HasError);
        if (dialog.HasError) Assert.Equal(PathGuard.WhyNotPlain(name), dialog.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(Typed))]
    public void WhatTheNameDialogLetsThrough_TheStoresTake_AndWhatItStops_TheyRefuse(string typed)
    {
        var dialog = new ProfileNameInputDialogViewModel(typed, _ => false);
        string name = dialog.NormalizedName;
        if (name.Length == 0) return;
        string bbsName = $"{name} {_tag}";   // a BBS of this test's own; a tag after the name keeps it as plain or not as it was

        if (dialog.CanSave)
        {
            _profiles.CreateProfile(Bbs, name);
            Assert.True(_profiles.Exists(Bbs, name));
            Assert.True(_bbs.Rename(Other, bbsName));
            Assert.True(_bbs.Exists(bbsName));
            Assert.Null(RealmCatalog.NameProblem(name, Array.Empty<string>()));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => _profiles.CreateProfile(Bbs, name));
            Assert.False(_bbs.Rename(Other, name));
            Assert.NotNull(RealmCatalog.NameProblem(name, Array.Empty<string>()));
        }
    }

    // ----- Names that arrive from outside ---------

    [Fact]
    public void ACommandLineProfileIsOnlyEverOneOfThoseListed()
    {
        // --profile is matched against the profiles found on disk and never joined
        // onto a path, so a token that is no listed profile's loads nothing.
        List<ProfileRef> listed = _profiles.ListAll().ToList();

        foreach (string token in new[] { "..", "../kept", $"{Bbs}/..", $"../{Bbs}/kept", $"{Bbs}/../{Bbs}/kept", "/etc/passwd", "a\\b" })
            Assert.Null(StartupOptions.ResolveToken(token, listed, out _));
        Assert.Equal(new ProfileRef(Bbs, "kept"), StartupOptions.ResolveToken($"{Bbs}/kept", listed, out _));
    }

    [Fact]
    public void ARecentOrLastUsedEntryThatIsNoProfilesName_HasNoProfile_SoItIsDroppedAndNeverLoaded()
    {
        // The recent list is pruned by Exists, and the last-used profile is loaded
        // inside a catch that falls back to the default profile. This entry, joined
        // on unchecked, is a real profile's file reached the long way round: it
        // would be listed, loaded, and every save of the session written through it.
        var planted = new ProfileRef("..", $"../{Path.GetFileName(AppPaths.BbsDir)}/{Bbs}/profiles/kept");
        Assert.True(File.Exists(Path.GetFullPath(
            Path.Combine(AppPaths.BbsDir, planted.Bbs, "profiles", planted.Name, "profile.json"))));

        Assert.False(_profiles.Exists(planted.Bbs, planted.Name));
        Assert.Throws<ArgumentException>(() => _profiles.Load(planted.Bbs, planted.Name));
        Assert.Null(_profiles.CurrentProfileName);
    }

    [Fact]
    public void OnlyFoldersAreListed_EachUnderItsOwnName()
    {
        File.WriteAllText(Path.Combine(AppPaths.BbsDir, $"file{_tag}"), "not a BBS");
        Directory.CreateDirectory(Path.Combine(ProfilesDir, "no profile file"));

        Assert.Contains(Bbs, _bbs.ListNames());
        Assert.DoesNotContain($"file{_tag}", _bbs.ListNames());
        Assert.Equal(new[] { new ProfileRef(Bbs, "kept") }, _profiles.ListAll().Where(r => r.Bbs == Bbs));
        File.Delete(Path.Combine(AppPaths.BbsDir, $"file{_tag}"));
    }

    // ----- Profiles made before names were checked ---------

    // A profile's folder under a name that is one path segment on this platform and
    // that nothing would be given today, made the way an older client left it.
    private string AnOlderProfileNamed(string odd, string? onBbs = null)
    {
        string folder = Path.Combine(AppPaths.BbsDir, onBbs ?? Bbs, "profiles", odd);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(ProfilesDir, "kept", "profile.json"), Path.Combine(folder, "profile.json"));
        File.WriteAllText(Path.Combine(folder, "monsters_overrides.set.json"), "{}");
        return folder;
    }

    public static IEnumerable<object[]> OddNames() => new[]
    {
        new object[] { "Sir: Bob" }, new object[] { "a\\b" }, new object[] { "Bob Jr." }, new object[] { "trailing " },
        new object[] { "CON" }, new object[] { "tab\tbed" },
    };

    [Theory]
    [MemberData(nameof(OddNames))]
    public void AProfileUnderAnOlderOddName_IsListedLoadedSavedAndRenamed(string odd)
    {
        if (OperatingSystem.IsWindows()) return;   // none of these can be a folder's name there
        string folder = AnOlderProfileNamed(odd);
        Assert.False(PathGuard.IsPlainName(odd));

        Assert.Contains(new ProfileRef(Bbs, odd), _profiles.ListAll());
        Assert.True(_profiles.Exists(Bbs, odd));
        _profiles.Load(Bbs, odd);
        Assert.Equal(odd, _profiles.CurrentProfileName);
        _profiles.Current!.Realm = "saved";
        _profiles.Save();
        Assert.Contains("\"saved\"", File.ReadAllText(Path.Combine(folder, "profile.json")));

        // Renamed while loaded, to a name it can be given today.
        string renamed = $"renamed {_tag}";
        _profiles.MoveProfile(Bbs, odd, Bbs, renamed);
        Assert.Equal(renamed, _profiles.CurrentProfileName);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(ProfilesDir, renamed, "monsters_overrides.set.json")));
        Assert.Empty(Said);
    }

    [Theory]
    [MemberData(nameof(OddNames))]
    public void AProfileUnderAnOlderOddName_NotLoaded_IsCopiedRenamedMovedToAnotherBbsAndDeleted(string odd)
    {
        if (OperatingSystem.IsWindows()) return;
        string folder = AnOlderProfileNamed(odd);

        _profiles.AssignRealm(new ProfileRef(Bbs, odd), "assigned");
        Assert.Equal("assigned", _profiles.RealmOf(new ProfileRef(Bbs, odd)));
        _profiles.CopyProfile(Bbs, odd, "a copy");
        Assert.True(_profiles.Exists(Bbs, "a copy"));

        // To another BBS under the name it has: it need not be renamed first.
        _profiles.MoveProfile(Bbs, odd, Other, odd);
        Assert.False(Directory.Exists(folder));
        Assert.True(_profiles.Exists(Other, odd));
        _profiles.MoveProfile(Other, odd, Other, "renamed");
        Assert.True(_profiles.Exists(Other, "renamed"));

        AnOlderProfileNamed(odd);
        _profiles.DeleteProfile(Bbs, odd);
        Assert.False(Directory.Exists(folder));
        Assert.True(_profiles.Exists(Bbs, "kept"));
        Assert.Empty(Said);
    }

    [Theory]
    [MemberData(nameof(OddNames))]
    public void AnOddNameIsNotGivenToAnythingNew_AndAnOlderProfileIsNotRenamedToAnother(string odd)
    {
        if (OperatingSystem.IsWindows()) return;
        string other = odd + "x:";
        AnOlderProfileNamed(odd);

        Assert.Throws<ArgumentException>(() => _profiles.CreateProfile(Bbs, other));
        Assert.Throws<ArgumentException>(() => _profiles.CopyProfile(Bbs, "kept", other));
        Assert.Throws<ArgumentException>(() => _profiles.MoveProfile(Bbs, "kept", Bbs, other));
        Assert.Throws<ArgumentException>(() => _profiles.MoveProfile(Bbs, odd, Bbs, other));
        // Onto the older one is a clash like any other, not a refusal of its name.
        Assert.Throws<IOException>(() => _profiles.CreateProfile(Bbs, odd));
        Assert.Throws<IOException>(() => _profiles.MoveProfile(Bbs, "kept", Bbs, odd));
        Assert.True(_profiles.Exists(Bbs, odd));
        Assert.True(_profiles.Exists(Bbs, "kept"));
    }

    [Fact]
    public void ABbsUnderAnOlderOddName_IsListedReadSavedUsedForItsProfilesRenamedAndDeleted()
    {
        if (OperatingSystem.IsWindows()) return;
        string odd = $"Para: PvE {_tag}";
        Directory.CreateDirectory(Path.Combine(AppPaths.BbsDir, odd));
        File.Copy(AppPaths.BbsProfileFile(Bbs), Path.Combine(AppPaths.BbsDir, odd, "bbs.json"));
        AnOlderProfileNamed("Bob", onBbs: odd);

        Assert.Contains(odd, _bbs.ListNames());
        BbsProfile board = Assert.IsType<BbsProfile>(_bbs.Get(odd));
        Assert.Equal(odd, board.Name);
        board.Host = "changed";
        _bbs.Save(board);
        Assert.Equal("changed", _bbs.Get(odd)!.Host);

        // Its characters load and save, and a new one can be made on it.
        _profiles.Load(odd, "Bob");
        _profiles.Save();
        _profiles.CreateProfile(odd, "Alice");
        Assert.True(_profiles.Exists(odd, "Alice"));
        _profiles.LoadDefaultProfile(onBbs: odd);
        Assert.Equal(odd, _profiles.CurrentBbsName);

        // It can't be renamed to another odd name, and can to a plain one.
        Assert.False(_bbs.Rename(odd, odd + "?:"));
        string plain = $"Para PvE {_tag}";
        Assert.True(_bbs.Rename(odd, plain));
        _profiles.RenameBbs(odd, plain);
        Assert.True(_profiles.Exists(plain, "Bob"));
        Assert.Equal(plain, _profiles.CurrentBbsName);

        _bbs.Delete(plain);
        Assert.False(_bbs.Exists(plain));
        Assert.True(_bbs.Exists(Bbs));
    }

    [Fact]
    public void ARealmUnderAnOlderOddName_KeepsItsDataAndCharacters_AndIsRenamedOrRemoved()
    {
        if (OperatingSystem.IsWindows()) return;
        const string odd = "PvP: hard";
        BbsProfile board = _bbs.Get(Bbs)!;
        board.Realms.Add(new RealmProfile { Name = odd });
        board.Realms.Add(new RealmProfile { Name = "spare: one" });
        _bbs.Save(board);
        string data = Path.Combine(AppPaths.RealmFolder(Bbs, odd), "players.json");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        File.WriteAllText(data, "{}");
        _profiles.AssignRealm(new ProfileRef(Bbs, "kept"), odd);
        var realms = new RealmCatalog(_bbs, _profiles);

        // Not to another odd name; to a plain one, with its folder and its character.
        Assert.NotNull(realms.Rename(Bbs, odd, "PvP: harder"));
        Assert.True(File.Exists(data));
        Assert.Null(realms.Rename(Bbs, odd, "PvP hard"));
        Assert.True(File.Exists(Path.Combine(AppPaths.RealmFolder(Bbs, "PvP hard"), "players.json")));
        Assert.Equal("PvP hard", _profiles.RealmOf(new ProfileRef(Bbs, "kept")));

        // One that never collected anything has no folder, and is renamed and
        // removed all the same.
        Assert.Null(realms.Rename(Bbs, "spare: one", "spare"));
        Assert.True(realms.Remove(Bbs, "spare"));
        Assert.True(realms.Remove(Bbs, "PvP hard"));
        Assert.False(Directory.Exists(AppPaths.RealmFolder(Bbs, "PvP hard")));
        Assert.True(_bbs.Exists(Bbs));
    }

    [Fact]
    public void ARealmInABbsFileWhoseNameLeadsElsewhere_IsRemovedFromTheFileAndNothingElseIsTouched()
    {
        BbsProfile board = _bbs.Get(Bbs)!;
        board.Realms.Add(new RealmProfile { Name = ".." });
        _bbs.Save(board);
        string canary = Path.Combine(AppPaths.BbsDir, Bbs, "canary.txt");
        File.WriteAllText(canary, "x");
        var realms = new RealmCatalog(_bbs, _profiles);

        Assert.True(realms.Remove(Bbs, ".."));

        // Unguarded, the realm's "folder" was the BBS's own, deleted with every character.
        Assert.True(File.Exists(canary));
        Assert.True(_profiles.Exists(Bbs, "kept"));
        Assert.DoesNotContain(_bbs.Get(Bbs)!.Realms, r => r.Name == "..");
    }

    // ----- Loops and their folders ---------

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../up")]
    [InlineData("a/b")]
    [InlineData("/abs")]
    [InlineData("C:x")]
    public void ALoopOfAnyNameIsSavedAsOneFileInsideItsSetsLoopsFolder(string name)
    {
        var cache = new GameDataCache(AppPaths.GameDataRoot);
        var graph = new RoomGraphManager(cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        loops.LoadAll(Set);
        string folder = AppPaths.GameDataSetLoopsFolder(Set);

        loops.Save(new Loop(name, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        string file = Assert.Single(Directory.EnumerateFiles(Path.Combine(AppPaths.GameDataRoot, Set), "*", SearchOption.AllDirectories));
        Assert.Equal(folder, Path.GetDirectoryName(file));
        Assert.EndsWith(LoopManager.LoopFileSuffix, file);
        Assert.Equal(new[] { Set }, Directory.EnumerateDirectories(AppPaths.GameDataRoot, $"*{_tag}*").Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../up")]
    [InlineData("a/../../b")]
    [InlineData("/abs/olute")]
    [InlineData("\\abs")]
    [InlineData("a\\..\\..\\b")]
    public void ANavFolderOfAnyNameIsTheLoopsFolderOrAFolderInsideIt(string folder)
    {
        string root = AppPaths.GameDataSetLoopsFolder(Set);

        string dir = NavFolders.ToDirectory(root, folder);

        Assert.True(dir == root || PathGuard.IsInside(dir, root), $"{folder} gave {dir}");
    }
}
