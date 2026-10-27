using System;
using System.IO;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// A realm can name a game-data set that isn't installed on this machine (a set
// imported on another computer, or one deleted since). The Settings BBS tab used
// to show such a realm as the global default, so an OK after an edit to any other
// field of the realm saved null over the set it chose. The name has to survive
// until another set is picked.
//
// The board is saved under the scratch data root; the installed sets come from a
// game-data folder of the test's own. Both are deleted in Dispose.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsSectionRealmGameDataSetTests : IDisposable
{
    private const string Installed = "alpha";
    private const string Missing = "gone";

    private readonly string _board = "!mudplay-test-realmset-" + Path.GetRandomFileName();
    private readonly string _gameData = Path.Combine(Path.GetTempPath(), "mudplay-realmset-" + Path.GetRandomFileName());
    private readonly BbsSectionViewModel _vm;

    public BbsSectionRealmGameDataSetTests()
    {
        ScratchRoot.Require();
        Directory.CreateDirectory(Path.Combine(_gameData, Installed));
        BbsProfileStore store = new();
        store.Save(new BbsProfile
        {
            Name = _board,
            Host = "a.example",
            Port = 23,
            Realms = { new RealmProfile { Name = "Main", ActiveGameDataSet = Missing } },
        });
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile(onBbs: _board);
        _vm = new BbsSectionViewModel(store, profile, new PasswordProtector(), new DisplayConfig(),
            new SettingsService(), new GameDataCache(_gameData));
        _vm.SelectedBbsName = _board;
    }

    public void Dispose()
    {
        _vm.Dispose();
        new BbsProfileStore().Delete(_board);
        try { Directory.Delete(_gameData, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a scratch folder */ }
    }

    private RealmProfile Saved() => new BbsProfileStore().Get(_board)!.RealmFor("Main")!;

    [Fact]
    public void UninstalledSet_ShownAsNotInstalled_AndKeptWhenAnotherFieldIsSaved()
    {
        Assert.Equal("gone (not installed)", _vm.RealmGameDataSet);
        Assert.Contains("gone (not installed)", _vm.GameDataSetOptions);

        _vm.GameEntryCommand = "F";
        _vm.Apply();

        RealmProfile saved = Saved();
        Assert.Equal(Missing, saved.ActiveGameDataSet);
        Assert.Equal("F", saved.GameEntryCommand);
    }

    [Fact]
    public void PickingAnInstalledSet_SavesIt()
    {
        _vm.RealmGameDataSet = Installed;
        _vm.Apply();

        Assert.Equal(Installed, Saved().ActiveGameDataSet);
    }

    [Fact]
    public void PickingTheGlobalDefault_SavesNoSet()
    {
        _vm.RealmGameDataSet = BbsSectionViewModel.GlobalDefaultSet;
        _vm.Apply();

        Assert.Null(Saved().ActiveGameDataSet);
    }
}
