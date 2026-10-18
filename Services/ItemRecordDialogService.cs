using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.Services;

// Opens the item record (edit) dialog by item Number, modelessly, from anywhere
// outside the Game Data Browser (the Item Finder, the Roomba Master List, the
// Workshop's item rows, the spell book), and saves what it hands back. Mirrors the browser Items tab's open flow
// (ItemsSectionViewModel.OpenEditAsync) but keyed on a Number instead of a browser
// row, and shares the heavy read-only view assembly via ItemMdbViewBuilder. The
// small orchestration is duplicated on purpose: the browser path is coupled to its
// GameDataRow + Reload, this one resolves name/tier from the Number and has no grid
// to reload. Single-instance: re-opening the shown record is a no-op; another swaps.
public sealed class ItemRecordDialogService
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver _resolver;
    // Shows the dialog and completes with what it hands back: the record on OK,
    // null on Cancel. DialogService.OpenWindowAsync in the client.
    private readonly Func<ItemEditDialogViewModel, Task<ItemEditResult?>> _show;
    private readonly ConfirmService _confirm;
    private readonly ItemOverlaySeedStore _overlaySeed;
    private readonly ItemSourceIndex _itemSources;

    private ItemEditDialogViewModel? _openItemVm;

    public ItemRecordDialogService(
        GameDataCache cache, SettingsResolver resolver,
        Func<ItemEditDialogViewModel, Task<ItemEditResult?>> show, ConfirmService confirm,
        ItemOverlaySeedStore overlaySeed, ItemSourceIndex itemSources)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(overlaySeed);
        ArgumentNullException.ThrowIfNull(itemSources);
        _cache = cache;
        _resolver = resolver;
        _show = show;
        _confirm = confirm;
        _overlaySeed = overlaySeed;
        _itemSources = itemSources;
    }

    public async Task OpenAsync(int itemNumber)
    {
        if (itemNumber <= 0) return;
        string wcc = itemNumber.ToString(CultureInfo.InvariantCulture);

        // Re-opening the record already showing is a no-op — don't tear down edits.
        if (_openItemVm is not null && string.Equals(_openItemVm.WccNoStr, wcc, StringComparison.Ordinal))
            return;

        // 4-tier merged overlay over the realm-flavoured Defaults seed, and the tier
        // it currently resolves from (drives the dialog's tier picker).
        ItemOverlay seedDefaults = _overlaySeed.GetOverlay(itemNumber);
        ItemOverlay existing = _resolver.ResolveGameData<ItemOverlay>("Items", wcc, seedDefaults) ?? seedDefaults;
        SettingsTier currentTier = _resolver.GetGameDataSourceTier("Items", wcc);

        // Built at the neutral retail charm (50) to match the dialog's charm
        // picker default; the picker re-prices the shop rows via shopSalesForCharm.
        ItemMdbView mdb = new ItemMdbViewBuilder(_cache, 50).Build(wcc);
        GameDataCache cache = _cache;
        IReadOnlyList<ShopSaleRow> ShopsForCharm(int charm) =>
            new ItemMdbViewBuilder(cache, charm).Build(wcc).Shops;
        ChestContents? chest = ChestContentsReader.Read(_cache, itemNumber);
        IReadOnlyList<ItemSource>? containerSources = _itemSources.ContainersOf(itemNumber);
        IReadOnlyList<ItemGiver>? givers = _itemSources.GiversOf(itemNumber);

        // On-use / proc message editing — same surface the browser Items tab wires,
        // via the shared ItemMessageDialogService (item-claimed messages live with
        // the item, hidden from the Messages tab).
        ItemMessageDialogService? itemMsg = AppServices.CurrentOrNull?.ItemMessage;
        Func<Task<string?>>? editMsg = itemMsg is not null ? () => itemMsg.OpenAsync(itemNumber) : null;
        string? msgSummary = itemMsg?.SummaryFor(itemNumber);

        ItemEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          _cache.FindNameByNumber("Items", itemNumber) ?? string.Empty,
            existing:         existing,
            currentTier:      currentTier,
            mdbInfo:          mdb.OtherInfo,
            shops:            mdb.Shops,
            writableTiers:    _resolver.WritableTiers(),
            isLight:          mdb.IsLight,
            isContainer:      mdb.IsContainer,
            chest:            chest,
            containerSources: containerSources,
            givers:           givers,
            shopSalesForCharm: ShopsForCharm,
            droppedBy:        mdb.DroppedBy,
            placedIn:         mdb.PlacedIn,
            roomCommands:     mdb.RoomCommands,
            castsSpells:      mdb.CastsSpells,
            summons:          mdb.Summons,
            teleportsTo:      mdb.TeleportsTo,
            editAttachedMessage:    editMsg,
            attachedMessageSummary: msgSummary,
            exitUses:         new ItemMdbViewBuilder(_cache, 50).ExitUsesOf(itemNumber));

        ItemEditDialogViewModel? previous = _openItemVm;
        _openItemVm = vm;
        previous?.CancelCommand.Execute(null);

        ItemEditResult? result;
        try
        {
            result = await _show(vm);
        }
        finally
        {
            if (ReferenceEquals(_openItemVm, vm)) _openItemVm = null;
        }
        if (result is null) return;

        // Installed-defaults reset or the field-by-field save — shared with the
        // browser's Items tab.
        await GameDataOverrideApplier.ApplyItemAsync(
            _resolver, _confirm, result.WccNoStr, result.Tier, result.Overlay, seedDefaults, result.Changed,
            result.MovedFrom);
    }
}
