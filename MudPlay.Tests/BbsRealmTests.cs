using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Named realms inside a BBS: a board saved before realms existed becomes one
// realm (named after the BBS) carrying its realm settings and collected data, and
// characters are assigned to realms by name.
public sealed class BbsRealmTests : IDisposable
{
    private readonly string _bbs = "realm-test-" + Path.GetRandomFileName();
    private readonly string _set = "realm-test-set-" + Path.GetRandomFileName();

    public BbsRealmTests() => ScratchRoot.Require();

    public void Dispose()
    {
        foreach (string folder in new[] { AppPaths.BbsFolder(_bbs), AppPaths.GameDataSetDir(_set) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void LegacyBbs_BecomesOneRealm_WithItsSettingsAndData()
    {
        string folder = AppPaths.BbsFolder(_bbs);
        Directory.CreateDirectory(folder);
        File.WriteAllText(AppPaths.BbsProfileFile(_bbs), $$"""
            { "Name": "{{_bbs}}", "Host": "example.org", "Port": 23,
              "ActiveGameDataSet": "{{_set}}", "GameEntryCommand": "P", "PlayerDiesAtHp": -40,
              "AutoRefineDeathFloor": false, "RunicCurrencyName": "quatloo" }
            """);
        File.WriteAllText(Path.Combine(folder, "players.json"), "[]");
        File.WriteAllText(Path.Combine(folder, $"monster_overrides.{_set}.json"), "{}");
        Directory.CreateDirectory(AppPaths.GameDataSetDir(_set));
        File.WriteAllText(AppPaths.LegacySetBossTimersFile(_set), "{}");

        BbsProfile bbs = new BbsProfileStore().Get(_bbs)!;

        RealmProfile realm = Assert.Single(bbs.Realms);
        Assert.Equal(_bbs, realm.Name);
        Assert.Equal(_set, realm.ActiveGameDataSet);
        Assert.Equal("P", realm.GameEntryCommand);
        Assert.Equal(-40, realm.PlayerDiesAtHp);
        Assert.False(realm.AutoRefineDeathFloor);
        Assert.Equal("quatloo", realm.RunicCurrencyName);

        string realmFolder = AppPaths.RealmFolder(_bbs, realm.Name);
        Assert.True(File.Exists(AppPaths.RealmPlayersFile(realmFolder)));
        Assert.False(File.Exists(Path.Combine(folder, "players.json")));
        Assert.True(File.Exists(Path.Combine(realmFolder, $"monster_overrides.{_set}.json")));
        Assert.True(File.Exists(AppPaths.RealmBossTimersFile(realmFolder)));

        // Saved with its realm, so the next load doesn't migrate again.
        Assert.Single(new BbsProfileStore().Get(_bbs)!.Realms);
    }

    [Fact]
    public void RealmFor_MatchesByName_ElseTheFirstRealm()
    {
        BbsProfile bbs = new() { Realms = { new() { Name = "PVE" }, new() { Name = "PVP" } } };

        Assert.Equal("PVP", bbs.RealmFor("pvp")!.Name);
        Assert.Equal("PVE", bbs.RealmFor(null)!.Name);
        Assert.Equal("PVE", bbs.RealmFor("gone")!.Name);
    }

    [Fact]
    public void RenamingARealm_RepointsItsCharacters()
    {
        new BbsProfileStore().Save(new BbsProfile { Name = _bbs, Realms = { new() { Name = "PVE" }, new() { Name = "PVP" } } });
        ProfileService profiles = new();
        profiles.AssignRealm(Seed("Priest"), "PVE");
        profiles.AssignRealm(Seed("Paladin"), "PVP");

        profiles.RenameRealm(_bbs, "PVE", "Classic");

        Assert.Equal("Classic", profiles.RealmOf(new ProfileRef(_bbs, "Priest")));
        Assert.Equal("PVP", profiles.RealmOf(new ProfileRef(_bbs, "Paladin")));
    }

    // A profile.json left as zero bytes by a power cut mid-write: asked for its realm
    // at startup, before the window was up, it threw and the client never started
    // (report Crash-20261029-161814). It answers as no realm; loading it still throws
    // with the file named, which the startup auto-load turns into the default profile
    // and a notice.
    [Fact]
    public void AProfileLeftAsZeroBytes_HasNoRealm_AndItsLoadSaysWhy()
    {
        ProfileRef monk = Seed("Monk");
        File.WriteAllBytes(AppPaths.CharacterProfileFile(_bbs, "Monk"), new byte[4096]);
        ProfileService profiles = new();

        Assert.Null(profiles.RealmOf(monk));
        InvalidDataException thrown = Assert.Throws<InvalidDataException>(() => profiles.Load(_bbs, "Monk"));
        Assert.Contains("profile.json", thrown.Message);
    }

    // A save is whole on the disk, with no temporary file left beside it.
    [Fact]
    public void ASavedProfile_ReadsBackWhole_AndLeavesNoTemporaryFile()
    {
        ProfileRef priest = Seed("Priest");
        new ProfileService().AssignRealm(priest, "PVE");

        Assert.Equal("PVE", new ProfileService().RealmOf(priest));
        string folder = Path.GetDirectoryName(AppPaths.CharacterProfileFile(_bbs, "Priest"))!;
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.NotEqual(0, File.ReadAllBytes(AppPaths.CharacterProfileFile(_bbs, "Priest"))[0]);
    }

    private ProfileRef Seed(string name)
    {
        string path = AppPaths.CharacterProfileFile(_bbs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, new CharacterProfile { Name = name });
        return new ProfileRef(_bbs, name);
    }

    [Fact]
    public void Catalog_AddRenameRemove()
    {
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = _bbs });   // one realm, named after the BBS
        ProfileService profiles = new();
        RealmCatalog realms = new(store, profiles);
        List<(string Bbs, string From, string To)> renamed = new();
        profiles.RealmRenamed += (bbs, from, to) => renamed.Add((bbs, from, to));

        string added = realms.Add(_bbs)!;
        Assert.Equal("Realm 2", added);
        profiles.AssignRealm(Seed("Priest"), added);
        Directory.CreateDirectory(AppPaths.RealmFolder(_bbs, added));
        File.WriteAllText(AppPaths.RealmPlayersFile(AppPaths.RealmFolder(_bbs, added)), "[]");

        Assert.NotNull(realms.Rename(_bbs, added, _bbs));   // name taken
        Assert.Null(realms.Rename(_bbs, added, "PVE"));
        Assert.True(File.Exists(AppPaths.RealmPlayersFile(AppPaths.RealmFolder(_bbs, "PVE"))));
        Assert.Equal("PVE", profiles.RealmOf(new ProfileRef(_bbs, "Priest")));
        Assert.Equal("Priest", Assert.Single(realms.CharactersOn(_bbs, "PVE")).Name);
        // Told once, for what is kept in the realm's folder to follow it.
        Assert.Equal((_bbs, added, "PVE"), Assert.Single(renamed));

        // Removing a realm deletes the characters on it and its data.
        Assert.True(realms.Remove(_bbs, "PVE"));
        Assert.Single(renamed);
        Assert.False(profiles.Exists(_bbs, "Priest"));
        Assert.False(Directory.Exists(AppPaths.RealmFolder(_bbs, "PVE")));
        Assert.False(realms.Remove(_bbs, _bbs));            // a BBS keeps one realm
        Assert.Single(store.Get(_bbs)!.Realms);
    }
}
