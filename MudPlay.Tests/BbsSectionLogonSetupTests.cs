using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Settings BBS tab's logon setups. Picking a realm's game data set fills in the
// setup that data was exported for, Anomaly, Paradigm or Stock, only where nothing
// is set yet; the Connection header's Apply setup puts one in over what's there.
//
// The board is saved under the scratch data root and the game data sets under its
// game-data folder; both are deleted in Dispose.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsSectionLogonSetupTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "");
    private readonly string _board;
    private readonly string _anomalySet;
    private readonly string _paradigmSet;
    private readonly ProfileService _profile;
    private BbsSectionViewModel _vm;

    public BbsSectionLogonSetupTests()
    {
        ScratchRoot.Require();
        _board = $"!mudplay-test-logonsetup-{_tag}";
        _anomalySet = WriteSet("anomaly", "Anomaly");
        _paradigmSet = WriteSet("paradigm", "Paradigm");
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = _board, Realms = { new RealmProfile { Name = "Main" } } });
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile(onBbs: _board);
        _vm = NewTab();
    }

    // The tab as the Settings window opens it, on the board.
    private BbsSectionViewModel NewTab()
    {
        BbsSectionViewModel vm = new(new BbsProfileStore(), _profile, new PasswordProtector(), new DisplayConfig(),
            new SettingsService(), new GameDataCache());
        vm.SelectedBbsName = _board;
        return vm;
    }

    public void Dispose()
    {
        _vm.Dispose();
        new BbsProfileStore().Delete(_board);
        foreach (string set in new[] { _anomalySet, _paradigmSet })
        {
            try { Directory.Delete(AppPaths.GameDataSetDir(set), recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    private string WriteSet(string kind, string custom)
    {
        string set = $"!mudplay-test-{kind}-{_tag}";
        string dir = AppPaths.GameDataSetDir(set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), $$"""[ { "Custom": "{{custom}}", "Legit": 2 } ]""");
        return set;
    }

    private IEnumerable<string> Sends() => _vm.MenuNavSteps.Select(s => s.Send).ToList();

    [Fact]
    public void ANewBoard_PickingAnAnomalySet_FillsTheHostPortAndItsLogonSteps()
    {
        Assert.Equal(["{username}", "{password}", "m"], Sends());

        _vm.RealmGameDataSet = _anomalySet;

        Assert.Equal("playanomalymud.com", _vm.Host);
        Assert.Equal(2324, _vm.Port);
        Assert.Equal(["{username}", "{password}", "1"], Sends());
    }

    [Fact]
    public void AHostTypedAndStepsEdited_StayWhenASetIsPicked()
    {
        _vm.Host = "my.board.example";
        _vm.MenuNavSteps[2].Send = "g";

        _vm.RealmGameDataSet = _paradigmSet;

        Assert.Equal("my.board.example", _vm.Host);
        Assert.Equal(["{username}", "{password}", "g"], Sends());
    }

    [Fact]
    public void ApplySetup_PutsItInOverWhatsThere_AndStockLeavesTheHostAlone()
    {
        _vm.Host = "my.board.example";
        _vm.MenuNavSteps[2].Send = "g";

        _vm.ApplyLogonSetupCommand.Execute("Paradigm");

        Assert.Equal("paramud.mudinfo.net", _vm.Host);
        Assert.Equal(2323, _vm.Port);
        Assert.Equal(["{username}", "{password}", "P", "1"], Sends());

        _vm.ApplyLogonSetupCommand.Execute("Stock");

        Assert.Equal("paramud.mudinfo.net", _vm.Host);
        Assert.Equal(["{username}", "{password}", "m"], Sends());
        _vm.Apply();
        Assert.Equal("paramud.mudinfo.net", new BbsProfileStore().Get(_board)!.Host);
    }

    // A login saved with the steps the client filled in shows the setup installed
    // data calls for; one the player applied by hand is saved as theirs and stays.
    [Fact]
    public void SavedStepsTheClientFilledIn_GiveWayToTheRealmsData_AndAppliedOnesStay()
    {
        _vm.Username = "hero";
        _vm.Apply();
        Assert.Equal("Stock", _profile.Current!.BbsCredentials![_board].AutoLogonSetup);

        BbsProfileStore store = new();
        BbsProfile board = store.Get(_board)!;
        board.Realms[0].ActiveGameDataSet = _anomalySet;
        store.Save(board);
        _vm.Dispose();
        _vm = NewTab();

        Assert.Equal(["{username}", "{password}", "1"], Sends());

        _vm.ApplyLogonSetupCommand.Execute("Stock");
        _vm.Apply();
        Assert.Null(_profile.Current!.BbsCredentials![_board].AutoLogonSetup);
        _vm.Dispose();
        _vm = NewTab();

        Assert.Equal(["{username}", "{password}", "m"], Sends());
    }
}
