using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The three places a name that can't be a folder still reached the holding folder
// for refused names, or said nothing: a realm's game-data overrides, a profile's
// set name, and a navigation folder a loop or Auto-Lair setup is filed in.
[Collection(ScratchRoot.NamesCollection)]
public sealed class FolderNameLeftoverTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "")[..8];
    private readonly List<string> _made = new();
    private readonly ProfileService _profiles = new();
    private readonly BbsProfileStore _bbs = new();

    private string Bbs => $"fl{_tag}";
    private string Set => $"flset{_tag}";
    private string RealmsDir => Path.Combine(AppPaths.BbsDir, Bbs, "Realms");
    private string LoopsRoot => Path.Combine(AppPaths.GameDataRoot, Set, "Loops");
    private string Holding(string parent) => Path.Combine(parent, AppPaths.RefusedName);

    public FolderNameLeftoverTests()
    {
        ScratchRoot.Require();
        _made.Add(Path.Combine(AppPaths.BbsDir, Bbs));
        _made.Add(Path.Combine(AppPaths.GameDataRoot, Set));
    }

    public void Dispose()
    {
        foreach (string path in _made)
        {
            // Only ever what this test made, by its tag, inside the scratch root.
            if (!path.Contains(_tag, StringComparison.Ordinal) || !ScratchRoot.Holds(path)) continue;
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    // ----- A realm's game-data overrides ---------

    private SettingsResolver ResolverOnRealm(string realm)
    {
        _bbs.Save(new BbsProfile { Name = Bbs, Host = "h", Realms = { new RealmProfile { Name = realm } } });
        _profiles.CreateProfile(Bbs, "c");
        _profiles.Load(Bbs, "c");
        return new SettingsResolver(new SettingsService(), _bbs, _profiles, () => Set);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("nested")]
    [InlineData("up-and-over")]
    [InlineData("blank")]
    public void ARealmWhoseNameCannotBeAFolder_HasNoRealmTier_AndNothingGoesToTheHoldingFolder(string kind)
    {
        string realm = kind switch
        {
            "nested" => $"n{_tag}/inner",
            "up-and-over" => $"../esc{_tag}",
            "blank" => "   ",
            _ => kind,
        };
        SettingsResolver resolver = ResolverOnRealm(realm);

        Assert.False(resolver.CanWriteAt(SettingsTier.Bbs));
        Assert.DoesNotContain(SettingsTier.Bbs, resolver.WritableTiers());
        Assert.Throws<InvalidOperationException>(() =>
            resolver.WriteGameDataAt(SettingsTier.Bbs, "Monsters", "1", new MonsterOverlay { Name = "x" }));
        Assert.False(Directory.Exists(Holding(RealmsDir)));

        // What another board's unusable realm left in the holding folder is not
        // read as this one's.
        Directory.CreateDirectory(Holding(RealmsDir));
        File.WriteAllText(Path.Combine(Holding(RealmsDir), $"monsters_overrides.{Set}.json"),
            "{ \"1\": { \"Name\": \"planted\" } }");
        Assert.Null(resolver.ResolveGameData("Monsters", "1", new MonsterOverlay()).Name);
        Assert.Equal(SettingsTier.Defaults, resolver.GetGameDataSourceTier("Monsters", "1"));
        Assert.Empty(resolver.GameDataOverrideIds("Monsters"));
    }

    [Fact]
    public void AnOrdinaryRealm_StillHasItsTier_InItsOwnFolder()
    {
        SettingsResolver resolver = ResolverOnRealm("home");

        Assert.True(resolver.CanWriteAt(SettingsTier.Bbs));
        resolver.WriteGameDataAt(SettingsTier.Bbs, "Monsters", "1", new MonsterOverlay { Name = "mine" });

        Assert.True(File.Exists(Path.Combine(RealmsDir, "home", $"monsters_overrides.{Set}.json")));
        Assert.Equal("mine", resolver.ResolveGameData("Monsters", "1", new MonsterOverlay()).Name);
        Assert.False(Directory.Exists(Holding(RealmsDir)));
    }

    // ----- A profile's set name ---------

    [Fact]
    public void ARefusedSetName_IsToldWithTheReasonAndTheWayOut()
    {
        string name = "a:b";
        string notice = GameDataCache.RefusedSetNotice(name, PathGuard.WhyNotPlain(name, "game-data set name")!);

        Assert.Contains("'a:b'", notice);
        Assert.Contains(PathGuard.WhyNotPlain(name, "game-data set name")!, notice);
        Assert.Contains("Game Data menu", notice);
    }

    // ----- Navigation folders ---------

    private (LoopManager Loops, LairManager Lairs, NavFolderManager Folders) NavOn()
    {
        Directory.CreateDirectory(LoopsRoot);
        var cache = new GameDataCache(AppPaths.GameDataRoot);
        var graph = new RoomGraphManager(cache);
        var loops = new LoopManager(new BfsMapper(graph), graph);
        var lairs = new LairManager();
        loops.LoadAll(Set);
        lairs.LoadAll(Set);
        return (loops, lairs, new NavFolderManager(loops, lairs));
    }

    private Loop NewLoop(string folder) =>
        new($"loop{_tag}", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }) { Folder = folder };

    // Folders no plain name could make, and one that leaves the folder it is in.
    public static IEnumerable<object[]> RefusedFolders() => new[]
    {
        new object[] { "ok/bad\0x" },
        new object[] { "C:x" },
        new object[] { "ok/a:b" },
        new object[] { "tail." },
        new object[] { "LPT1" },
        new object[] { AppPaths.RefusedName },
    };

    [Theory]
    [MemberData(nameof(RefusedFolders))]
    public void ALoopOrSetupInAFolderThatCannotBeOne_IsNotSaved_AndNothingIsMade(string folder)
    {
        (LoopManager loops, LairManager lairs, NavFolderManager folders) = NavOn();

        Assert.False(loops.Save(NewLoop(folder)));
        Assert.False(lairs.Save(new LairSetup($"lair{_tag}", Array.Empty<LairMarker>()) { Folder = folder }));
        Assert.False(folders.CreateFolder(folder));

        Assert.Empty(Directory.EnumerateFileSystemEntries(LoopsRoot, "*", SearchOption.AllDirectories));
        Assert.Null(loops.Get($"loop{_tag}"));
        Assert.NotNull(folders.WhyNotFolder(folder));
    }

    [Theory]
    [MemberData(nameof(RefusedFolders))]
    public void AMoveToAFolderThatCannotBeOne_LeavesTheLoopWhereItWas(string folder)
    {
        (LoopManager loops, LairManager lairs, _) = NavOn();
        Assert.True(loops.Save(NewLoop("kept")));
        Assert.True(lairs.Save(new LairSetup($"lair{_tag}", Array.Empty<LairMarker>()) { Folder = "kept" }));

        Assert.False(loops.Move($"loop{_tag}", folder));
        Assert.False(lairs.Move($"lair{_tag}", folder));

        Assert.Equal("kept", loops.Get($"loop{_tag}")!.Folder);
        Assert.Equal("kept", lairs.Get($"lair{_tag}")!.Folder);
        Assert.Equal(new[] { "kept" }, Directory.GetDirectories(LoopsRoot).Select(Path.GetFileName));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(LoopsRoot, "kept")).Length);
    }

    [Fact]
    public void AFolderUnderAnOddNameAlreadyOnDisk_IsStillUsed()
    {
        (LoopManager loops, _, NavFolderManager folders) = NavOn();
        if (OperatingSystem.IsWindows()) return;   // no such folder can be made there
        Directory.CreateDirectory(Path.Combine(LoopsRoot, "a:b"));

        Assert.Null(folders.WhyNotFolder("a:b"));
        Assert.True(loops.Save(NewLoop("a:b")));
        Assert.True(File.Exists(Path.Combine(LoopsRoot, "a:b", $"loop{_tag}.loop")));
    }

    [Fact]
    public void TheHoldingFolder_IsNeverListed_AndARefusedDeleteOrRenameLeavesItAlone()
    {
        (_, _, NavFolderManager folders) = NavOn();
        Directory.CreateDirectory(Path.Combine(Holding(LoopsRoot), "inner"));
        string kept = Path.Combine(Holding(LoopsRoot), "kept.txt");
        File.WriteAllText(kept, "kept");
        Directory.CreateDirectory(Path.Combine(LoopsRoot, "Real", "Deep"));

        Assert.Equal(new[] { "Real", "Real/Deep" }, folders.AllFolders.Order(StringComparer.Ordinal));

        // A refused name is sent to the holding folder by ToDirectory; a delete or
        // rename under one would have acted on it.
        Assert.False(folders.DeleteFolder("bad\0x"));
        Assert.False(folders.RenameFolder("bad\0x", "Moved"));
        Assert.False(folders.RenameFolder("Real", "C:x"));
        Assert.True(File.Exists(kept));
        Assert.True(Directory.Exists(Path.Combine(LoopsRoot, "Real", "Deep")));
        Assert.False(Directory.Exists(Path.Combine(LoopsRoot, "Moved")));
    }

    [Fact]
    public void TheFolderNameDialog_SaysWhyAndHoldsSave_OnlyWhenAskedToCheck()
    {
        Func<string, string?> check = f => NavFolders.WhyNotFolder(LoopsRoot, f);
        var checkedVm = new NavFolderNameDialogViewModel("New folder", "Name it.", "C:x", check);
        var gotoVm = new NavFolderNameDialogViewModel("New folder", "Name it.", "C:x");

        Assert.True(checkedVm.HasError);
        Assert.Equal(PathGuard.WhyNotPlain("C:x", "folder name"), checkedVm.ErrorMessage);
        Assert.False(checkedVm.SaveCommand.CanExecute(null));

        checkedVm.Name = "Sewers/Lower";
        Assert.False(checkedVm.HasError);
        Assert.True(checkedVm.SaveCommand.CanExecute(null));

        Assert.False(gotoVm.HasError);
        Assert.True(gotoVm.SaveCommand.CanExecute(null));
    }
}
