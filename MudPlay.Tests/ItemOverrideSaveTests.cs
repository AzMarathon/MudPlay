using System.IO;
using System.Text.Json.Nodes;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Batch;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// An item record saved from the Game Data item editor or batch editor: the tier
// saved at yields what was saved, field by field, whatever the tiers below say,
// and a record set back to its installed defaults shifts back to them (user,
// 2026-10-17). Only the tier saved at is written.
public sealed class ItemOverrideSaveTests : IDisposable
{
    private const string Id = "101";
    private readonly string _root;
    private readonly string _set = "item-save-" + Path.GetRandomFileName();
    private readonly string _bbs = "item-save-" + Path.GetRandomFileName();
    private readonly SettingsResolver _resolver;

    public ItemOverrideSaveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-item-save-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Items.json"),
            """[{ "Number": 101, "Name": "large shield", "Encum": 10, "ItemType": 2 }]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet(_set);

        BbsProfileStore boards = new();
        boards.Save(new BbsProfile { Name = _bbs, Host = "h", Realms = { new RealmProfile { Name = "home" } } });
        ProfileService profile = TestProfiles.Service();
        profile.CreateProfile(_bbs, "Tester");
        profile.Load(_bbs, "Tester");
        _resolver = new SettingsResolver(new SettingsService(), boards, profile, () => cache.ActiveSet);
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

    private ItemOverlay Resolved(ItemOverlay seed) => _resolver.ResolveGameData("Items", Id, seed);

    private SettingsTier Tier => _resolver.GetGameDataSourceTier("Items", Id);

    private string? GlobalText() => _resolver.ReadGameDataAt(SettingsTier.Global, "Items", Id)?.ToJsonString();

    // The editor as the Items tab opens it, on the record as it resolves, saving
    // at `tier` (the character tier unless named).
    private ItemEditDialogViewModel Editor(ItemOverlay seed, SettingsTier tier = SettingsTier.Character) => new(
        wccNoStr: Id, mdbName: "large shield", existing: Resolved(seed),
        currentTier: Tier, mdbInfo: Array.Empty<KeyValuePair<string, string>>(),
        shops: Array.Empty<ShopSaleRow>(),
        writableTiers: [SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global])
    {
        UseTier = tier,
    };

    private string[] HeldAt(SettingsTier tier) =>
        _resolver.ReadGameDataAt(tier, "Items", Id)?.Select(p => p.Key).Order().ToArray() ?? Array.Empty<string>();

    // OK pressed, and the result saved as the Items tab and the item record
    // window save it.
    private async Task Save(ItemEditDialogViewModel vm, ItemOverlay seed)
    {
        ItemEditResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.SaveCommand.Execute(null);
        Assert.NotNull(result);
        await GameDataOverrideApplier.ApplyItemAsync(_resolver, new ConfirmService(new DialogService()),
            result!.WccNoStr, result.Tier, result.Overlay, seed, result.Changed);
    }

    [Fact]
    public async Task AnUntick_AtTheCharacterTier_OverAGlobalTick_IsOff()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", Id, new ItemOverlay { AutoCollect = true });
        string? global = GlobalText();
        ItemEditDialogViewModel vm = Editor(seed);
        Assert.True(vm.AutoCollect);

        vm.AutoCollect = false;
        await Save(vm, seed);

        Assert.False(Resolved(seed).AutoCollect);
        Assert.Equal(SettingsTier.Character, Tier);
        Assert.Equal(global, GlobalText());
    }

    [Fact]
    public async Task BackToTheInstalledDefaults_OverADifferentGlobal_StillYieldsTheDefaults()
    {
        ItemOverlay seed = new() { AutoCollect = true, MaxToGet = "2" };
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", Id,
            new Dictionary<string, object> { ["MaxToGet"] = "5", ["AutoSell"] = true, ["MinToKeep"] = "1" });
        _resolver.WriteGameDataAt(SettingsTier.Character, "Items", Id, new Dictionary<string, object> { ["AutoStash"] = true });
        string? global = GlobalText();
        ItemEditDialogViewModel vm = Editor(seed);

        vm.MaxToGet = "2";
        vm.AutoSell = false;
        vm.MinToKeep = "";
        vm.AutoStash = false;
        await Save(vm, seed);

        ItemOverlay now = Resolved(seed);
        Assert.True(now.AutoCollect);
        Assert.Equal("2", now.MaxToGet);
        Assert.False(now.AutoSell ?? false);
        Assert.Null(now.MinToKeep);
        Assert.False(now.AutoStash ?? false);
        Assert.Equal(SettingsTier.Character, Tier);
        Assert.Equal(global, GlobalText());
        // Only what differs below is held: the fields at their defaults all round
        // are left out.
        JsonObject held = _resolver.ReadGameDataAt(SettingsTier.Character, "Items", Id)!;
        Assert.Equal(new[] { "AutoSell", "MaxToGet", "MinToKeep" }, held.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task BackToTheInstalledDefaults_WithNothingBelow_ClearsTheOverride()
    {
        ItemOverlay seed = new() { AutoCollect = true };
        _resolver.WriteGameDataAt(SettingsTier.Character, "Items", Id, new Dictionary<string, object> { ["AutoStash"] = true });
        ItemEditDialogViewModel vm = Editor(seed);

        vm.AutoStash = false;
        await Save(vm, seed);

        Assert.Equal(SettingsTier.Defaults, Tier);
        Assert.Equal(seed, Resolved(seed));
    }

    // One box changed leaves every other field resolving as it did, a Global rule
    // among them.
    [Fact]
    public async Task OneChange_LeavesTheOtherFieldsAsTheyResolved()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", Id, new ItemOverlay { AutoSell = true, MinToKeep = "3" });
        ItemOverlay before = Resolved(seed);
        ItemEditDialogViewModel vm = Editor(seed);

        vm.AutoCollect = true;
        await Save(vm, seed);

        ItemOverlay now = Resolved(seed);
        Assert.True(now.AutoCollect);
        Assert.Equal(before.AutoSell, now.AutoSell);
        Assert.Equal(before.MinToKeep, now.MinToKeep);
    }

    // The batch editor saves by the same rule.
    [Fact]
    public async Task TheBatchEditor_TurnsOffOverAGlobalTick_AndBackToDefaultsClears()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Bbs, "Items", Id, new ItemOverlay { AutoCollect = true });
        ConfirmService confirm = new(new DialogService());

        ItemBatchChanges turnOff = new() { AutoCollect = BatchToggle.Off };
        await GameDataOverrideApplier.ApplyItemAsync(_resolver, confirm, Id, SettingsTier.Character,
            turnOff.ApplyTo(Resolved(seed)), seed, turnOff.ChosenFields);
        Assert.False(Resolved(seed).AutoCollect);
        Assert.Equal(SettingsTier.Character, Tier);

        _resolver.ClearGameDataAt(SettingsTier.Bbs, "Items", Id);
        await GameDataOverrideApplier.ApplyItemAsync(_resolver, confirm, Id, SettingsTier.Character,
            turnOff.ApplyTo(Resolved(seed)), seed, turnOff.ChosenFields);
        Assert.Equal(SettingsTier.Defaults, Tier);
    }

    // A box left alone is not copied into the tier saved at: a Global value the
    // character never touched still follows Global.
    [Fact]
    public async Task ACharacterSave_DoesNotCopyAGlobalValueUp()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", Id, new Dictionary<string, object> { ["AutoSell"] = true });
        ItemEditDialogViewModel vm = Editor(seed);

        vm.AutoCollect = true;
        await Save(vm, seed);
        Assert.Equal(new[] { "AutoCollect" }, HeldAt(SettingsTier.Character));

        _resolver.ClearGameDataAt(SettingsTier.Global, "Items", Id);
        Assert.False(Resolved(seed).AutoSell ?? false);
        Assert.True(Resolved(seed).AutoCollect);
    }

    // Nor is a character's value copied down into Global.
    [Fact]
    public async Task AGlobalSave_DoesNotCopyACharactersValueDown()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Character, "Items", Id, new Dictionary<string, object> { ["AutoStash"] = true });
        ItemEditDialogViewModel vm = Editor(seed, SettingsTier.Global);
        Assert.True(vm.AutoStash);

        vm.AutoCollect = true;
        await Save(vm, seed);

        Assert.Equal(new[] { "AutoCollect" }, HeldAt(SettingsTier.Global));
        Assert.Equal(new[] { "AutoStash" }, HeldAt(SettingsTier.Character));
    }

    // At the realm tier, what lies below is the installed defaults and Global, not
    // the realm's own record: a realm record set back to its defaults clears.
    [Fact]
    public async Task ARealmRecord_SetBackToItsDefaults_Clears()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Bbs, "Items", Id, new Dictionary<string, object> { ["AutoStash"] = true });
        ItemEditDialogViewModel vm = Editor(seed, SettingsTier.Bbs);

        vm.AutoStash = false;
        await Save(vm, seed);

        Assert.Empty(HeldAt(SettingsTier.Bbs));
        Assert.Equal(SettingsTier.Defaults, Tier);
    }

    // The batch editor saves only the fields it was told to set.
    [Fact]
    public async Task TheBatchEditor_SavesOnlyItsChosenFields()
    {
        ItemOverlay seed = new();
        _resolver.WriteGameDataAt(SettingsTier.Global, "Items", Id, new Dictionary<string, object> { ["AutoSell"] = true });
        ItemBatchChanges turnOn = new() { AutoCollect = BatchToggle.On, ChangeMaxToGet = true, MaxToGet = "3" };

        await GameDataOverrideApplier.ApplyItemAsync(_resolver, new ConfirmService(new DialogService()), Id,
            SettingsTier.Character, turnOn.ApplyTo(Resolved(seed)), seed, turnOn.ChosenFields);

        Assert.Equal(new[] { "AutoCollect", "MaxToGet" }, HeldAt(SettingsTier.Character));
    }
}
