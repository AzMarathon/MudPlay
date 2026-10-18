using System.IO;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// The item record opened by number, from outside the Game Data Browser (the Roomba
// Master List, the Item Finder): OK saves the record at the tier picked in Use, by
// the same field-by-field rule the browser saves by, and Cancel saves nothing. The
// Roomba Master List's copy of this dialog used to drop what OK handed back.
public sealed class ItemRecordDialogServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _set = "item-record-" + Path.GetRandomFileName();
    private readonly string _bbs = "item-record-" + Path.GetRandomFileName();
    private readonly GameDataCache _cache;
    private readonly SettingsResolver _resolver;

    public ItemRecordDialogServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-item-record-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Items.json"),
            """[{ "Number": 101, "Name": "large shield", "Encum": 10, "ItemType": 2 }]""");
        _cache = new GameDataCache(_root);
        _cache.SwitchSet(_set);

        BbsProfileStore boards = new();
        boards.Save(new BbsProfile { Name = _bbs, Host = "h", Realms = { new RealmProfile { Name = "home" } } });
        ProfileService profile = TestProfiles.Service();
        profile.CreateProfile(_bbs, "Tester");
        profile.Load(_bbs, "Tester");
        _resolver = new SettingsResolver(new SettingsService(), boards, profile, () => _cache.ActiveSet);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
        try { File.Delete(AppPaths.OverrideFile(SettingsTier.Global, null, "Items", _set)); }
        catch { /* best-effort */ }
    }

    // The service as the client builds it, with the window standing in for a user
    // who does `act` and then presses OK (or, act returning false, Cancel).
    private ItemRecordDialogService Service(Func<ItemEditDialogViewModel, bool> act) => new(
        _cache, _resolver,
        vm =>
        {
            ItemEditResult? result = null;
            vm.CloseRequested += r => result = r;
            if (act(vm)) vm.SaveCommand.Execute(null);
            else vm.CancelCommand.Execute(null);
            return Task.FromResult(result);
        },
        new ConfirmService(new DialogService()), new ItemOverlaySeedStore(),
        new ItemSourceIndex(_cache, new TBInfoStore(_cache)));

    private ItemOverlay Resolved() => _resolver.ResolveGameData("Items", "101", new ItemOverlay());

    [Fact]
    public async Task Ok_SavesAtTheTierPicked()
    {
        await Service(vm =>
        {
            vm.UseTier = SettingsTier.Global;
            vm.AutoCollect = true;
            vm.MaxToGet = "3";
            return true;
        }).OpenAsync(101);

        Assert.True(Resolved().AutoCollect);
        Assert.Equal("3", Resolved().MaxToGet);
        Assert.Equal(SettingsTier.Global, _resolver.GetGameDataSourceTier("Items", "101"));
        Assert.Null(_resolver.ReadGameDataAt(SettingsTier.Character, "Items", "101"));
    }

    // Saved by the field-by-field rule: an untick at the character tier over the
    // Global tick is off.
    [Fact]
    public async Task Ok_SavesByTheFieldByFieldRule()
    {
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101",
            new Dictionary<string, object> { ["AutoCollect"] = true });

        await Service(vm =>
        {
            vm.UseTier = SettingsTier.Character;
            vm.AutoCollect = false;
            return true;
        }).OpenAsync(101);

        Assert.False(Resolved().AutoCollect);
        Assert.Equal(SettingsTier.Character, _resolver.GetGameDataSourceTier("Items", "101"));
    }

    [Fact]
    public async Task Cancel_SavesNothing()
    {
        await Service(vm =>
        {
            vm.AutoCollect = true;
            return false;
        }).OpenAsync(101);

        Assert.Equal(SettingsTier.Defaults, _resolver.GetGameDataSourceTier("Items", "101"));
    }
}
