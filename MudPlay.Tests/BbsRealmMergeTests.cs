using System.IO;
using System.Text.Json.Nodes;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Settings window holds a board for as long as it is open; the running client
// writes the board's realms meanwhile (the death floor, the count of deaths the
// standard list doesn't allow). OK saves the window's copy, and a board's realms
// merge one at a time, by name, so what was written meanwhile is kept and what was
// edited in the window still wins.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsRealmMergeTests : IDisposable
{
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey[] Listed = { new(1, 2189), new(1, 142) };
    private const string Set = "data-v1.11p";

    private readonly string _board = "!mudplay-test-realmmerge-" + Path.GetRandomFileName();
    private readonly BbsProfileStore _store = new();
    private readonly ProfileService _profile = TestProfiles.Service();

    public BbsRealmMergeTests()
    {
        ScratchRoot.Require();
        _store.Save(new BbsProfile
        {
            Name = _board,
            Host = "stock.example",
            Port = 23,
            Realms = { new RealmProfile { Name = "Main" }, new RealmProfile { Name = "Arena" } },
        });
        // Started on the test board, so the view model never opens (and writes back)
        // another test's.
        _profile.LoadDefaultProfile(onBbs: _board);
    }

    public void Dispose()
    {
        try { _store.Delete(_board); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup of a scratch board */ }
    }

    private BbsSectionViewModel OpenSettings()
    {
        BbsSectionViewModel vm = new(_store, _profile, new PasswordProtector(), new DisplayConfig(), new SettingsService(),
            new GameDataCache(AppPaths.GameDataRoot));
        vm.SelectedBbsName = _board;
        return vm;
    }

    // The running client marks the board, reading and saving it as the app does.
    private void TwoDeathsTheListDoesNotAllow()
    {
        StandardListContradictions board = new(
            () => _store.Get(_board) is { } bbs && bbs.RealmFor(_profile.Current?.Realm) is { } realm ? (bbs, realm) : null,
            () => Set, _store.Save);
        board.Note(Darkwood, new RoomKey(1, 500), Listed);
        board.Note(Darkwood, new RoomKey(1, 501), Listed);
        Assert.True(board.Marked);
    }

    // The death floor refined during play, as DeathFloorTracer saves it.
    private void DeathFloorRefinedTo(int hp)
    {
        BbsProfile bbs = _store.Get(_board)!;
        bbs.RealmFor("Main")!.PlayerDiesAtHp = hp;
        _store.Save(bbs);
    }

    private BbsProfile OnFile => new BbsProfileStore().Get(_board)!;

    [Fact]
    public void ACountWrittenWhileSettingsIsOpen_SurvivesAnOkThatEditsAnotherRealmField()
    {
        using BbsSectionViewModel vm = OpenSettings();
        Assert.Equal("Main", vm.SelectedRealmName);

        TwoDeathsTheListDoesNotAllow();
        DeathFloorRefinedTo(-40);

        vm.HangupPenaltyEnabled = true;
        vm.Apply();

        RealmProfile main = OnFile.RealmFor("Main")!;
        Assert.True(main.HangupPenaltyEnabled);
        Assert.Equal((2, Set), (main.DeathsContradictingStandardArrivalRooms, main.DeathsContradictingStandardArrivalRoomsOn));
        Assert.Equal(-40, main.PlayerDiesAtHp);
    }

    [Fact]
    public void AnEditInSettings_ToTheFieldWrittenMeanwhile_Wins()
    {
        using BbsSectionViewModel vm = OpenSettings();
        DeathFloorRefinedTo(-40);

        vm.PlayerDiesAtHp = -30;
        vm.Apply();

        Assert.Equal(-30, OnFile.RealmFor("Main")!.PlayerDiesAtHp);
    }

    [Fact]
    public void ARealmAddedInSettings_IsSaved_BesideTheCountWrittenMeanwhile()
    {
        using BbsSectionViewModel vm = OpenSettings();
        TwoDeathsTheListDoesNotAllow();

        vm.AddRealmCommand.Execute(null);
        string added = vm.SelectedRealmName!;
        vm.Apply();

        BbsProfile onFile = OnFile;
        Assert.Equal(new[] { "Main", "Arena", added }, onFile.Realms.Select(r => r.Name));
        Assert.Equal(2, onFile.Realms[0].DeathsContradictingStandardArrivalRooms);
    }

    // Settings removes a realm from its copy and saves it (Remove realm, then OK).
    // The running client counted on it meanwhile; a realm removed stays removed,
    // as its characters and data go with it.
    [Fact]
    public void ARealmRemovedInSettings_StaysRemoved_AndTheOthersKeepWhatWasWrittenMeanwhile()
    {
        BbsProfile settingsCopy = _store.Get(_board)!;
        BbsProfile running = _store.Get(_board)!;
        running.RealmFor("Main")!.PlayerDiesAtHp = -40;
        running.RealmFor("Arena")!.PlayerDiesAtHp = -60;
        _store.Save(running);

        settingsCopy.Realms.RemoveAll(r => r.Name == "Arena");
        settingsCopy.Host = "new.example";
        _store.Save(settingsCopy);

        BbsProfile onFile = OnFile;
        Assert.Equal(new[] { "Main" }, onFile.Realms.Select(r => r.Name));
        Assert.Equal(-40, onFile.Realms[0].PlayerDiesAtHp);
        Assert.Equal("new.example", onFile.Host);
    }

    // A realm renamed in Settings is the same realm under its new name: what the
    // running client wrote to it under the old name meanwhile goes with it.
    [Fact]
    public void ARealmRenamedInSettings_KeepsTheCountWrittenToItsOldNameMeanwhile()
    {
        using BbsSectionViewModel vm = OpenSettings();
        vm.RealmName = "Prime";
        Assert.Equal("Prime", vm.SelectedRealmName);

        TwoDeathsTheListDoesNotAllow();
        DeathFloorRefinedTo(-40);
        vm.Apply();

        BbsProfile onFile = OnFile;
        Assert.Equal(new[] { "Prime", "Arena" }, onFile.Realms.Select(r => r.Name));
        Assert.Equal((2, Set, -40), (onFile.Realms[0].DeathsContradictingStandardArrivalRooms,
            onFile.Realms[0].DeathsContradictingStandardArrivalRoomsOn, onFile.Realms[0].PlayerDiesAtHp));
    }

    // Two renames of one realm, to different names: Profile Management renames it
    // at once, and Settings, open since before, renames it again on OK. The first
    // rename saved stands, as its data folder and characters already moved with
    // it; the later one is dropped, and there is no second realm.
    [Fact]
    public void TwoRenamesOfOneRealm_TheFirstSavedStands_AndNoRealmIsLeftBehind()
    {
        using BbsSectionViewModel vm = OpenSettings();
        Assert.Null(new RealmCatalog(_store, _profile).Rename(_board, "Main", "Prime"));

        vm.RealmName = "First";
        vm.Apply();

        Assert.Equal(new[] { "Prime", "Arena" }, OnFile.Realms.Select(r => r.Name));
    }

    // Each side removed a different realm, which merged would leave none; a board
    // always has one, and this copy's stand rather than a fresh default realm.
    [Fact]
    public void TwoRemovalsOfDifferentRealms_LeaveThisCopysRealms_NotNone()
    {
        BbsProfile first = _store.Get(_board)!;
        BbsProfile second = _store.Get(_board)!;
        first.Realms.RemoveAll(r => r.Name == "Arena");
        _store.Save(first);
        second.Realms.RemoveAll(r => r.Name == "Main");
        _store.Save(second);

        Assert.Equal(new[] { "Arena" }, OnFile.Realms.Select(r => r.Name));
    }

    // A copy held a long time (the running client's, or any reader's) saved after
    // Profile Management removed a realm does not bring the realm back, though it
    // wrote to it.
    [Fact]
    public void ALongHeldCopy_SavedAfterARealmWasRemoved_DoesNotBringItBack()
    {
        BbsProfile held = _store.Get(_board)!;
        Assert.True(new RealmCatalog(_store, _profile).Remove(_board, "Arena"));

        held.RealmFor("Arena")!.PlayerDiesAtHp = -60;
        held.RealmFor("Main")!.PlayerDiesAtHp = -40;
        _store.Save(held);

        BbsProfile onFile = OnFile;
        Assert.Equal(new[] { "Main" }, onFile.Realms.Select(r => r.Name));
        Assert.Equal(-40, onFile.Realms[0].PlayerDiesAtHp);
    }

    // Only the arrays named as keyed merge record by record; any other is still
    // taken whole from the side that changed it.
    [Fact]
    public void Merge_KeyedRecordsMergeByName_OtherArraysWhole()
    {
        Dictionary<string, JsonThreeWayMerge.KeyedArray> keyed = new() { ["Realms"] = new("Name") };
        JsonNode baseline = JsonNode.Parse("""{ "Realms": [ { "Name": "A", "x": 1, "y": 1 }, { "Name": "B", "x": 1 } ], "List": [1, 2] }""")!;
        JsonNode mine     = JsonNode.Parse("""{ "Realms": [ { "Name": "A", "x": 2, "y": 1 }, { "Name": "B", "x": 1 } ], "List": [1, 2, 3] }""")!;
        JsonNode theirs   = JsonNode.Parse("""{ "Realms": [ { "Name": "A", "x": 1, "y": 9 }, { "Name": "B", "x": 5 } ], "List": [7] }""")!;

        JsonNode merged = JsonThreeWayMerge.Merge(baseline, mine, theirs, keyed)!;

        Assert.True(JsonNode.DeepEquals(merged, JsonNode.Parse(
            """{ "Realms": [ { "Name": "A", "x": 2, "y": 9 }, { "Name": "B", "x": 5 } ], "List": [1, 2, 3] }""")));
        // Without the key, the realms are one array like any other: mine, whole.
        Assert.True(JsonNode.DeepEquals(JsonThreeWayMerge.Merge(baseline, mine, theirs)!["Realms"], mine["Realms"]));
    }
}
