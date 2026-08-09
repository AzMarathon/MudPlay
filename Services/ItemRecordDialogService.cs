using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using FujinTerm.Game.GameData;
using FujinTerm.Models.GameData;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.Services;

// Opens the item record (edit) dialog by item Number, modelessly, from anywhere —
// today the Item Finder's double-click, which wants the record itself rather than
// the whole Game Data Browser. Mirrors the browser Items tab's open flow
// (ItemsSectionViewModel.OpenEditAsync) but keyed on a Number instead of a browser
// row, and shares the heavy read-only view assembly via ItemMdbViewBuilder. The
// small orchestration is duplicated on purpose: the browser path is coupled to its
// GameDataRow + Reload, this one resolves name/tier from the Number and has no grid
// to reload. Single-instance: re-opening the shown record is a no-op; another swaps.
public sealed class ItemRecordDialogService
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver _resolver;
    private readonly DialogService _dialogs;
    private readonly ItemOverlaySeedStore _overlaySeed;
    private readonly ItemSourceIndex _itemSources;
    private readonly Func<int> _playerCharm;

    private ItemEditDialogViewModel? _openItemVm;

    public ItemRecordDialogService(
        GameDataCache cache, SettingsResolver resolver, DialogService dialogs,
        ItemOverlaySeedStore overlaySeed, ItemSourceIndex itemSources, Func<int> playerCharm)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(overlaySeed);
        ArgumentNullException.ThrowIfNull(itemSources);
        ArgumentNullException.ThrowIfNull(playerCharm);
        _cache = cache;
        _resolver = resolver;
        _dialogs = dialogs;
        _overlaySeed = overlaySeed;
        _itemSources = itemSources;
        _playerCharm = playerCharm;
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

        ItemMdbView mdb = new ItemMdbViewBuilder(_cache, _playerCharm()).Build(wcc);
        ChestContents? chest = ChestContentsReader.Read(_cache, itemNumber);
        IReadOnlyList<ItemSource>? containerSources = _itemSources.ContainersOf(itemNumber);
        IReadOnlyList<ItemGiver>? givers = _itemSources.GiversOf(itemNumber);

        ItemEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          _cache.FindNameByNumber("Items", itemNumber) ?? string.Empty,
            existing:         existing,
            currentTier:      currentTier,
            mdbInfo:          mdb.OtherInfo,
            shops:            mdb.Shops,
            isLight:          mdb.IsLight,
            isContainer:      mdb.IsContainer,
            chest:            chest,
            containerSources: containerSources,
            givers:           givers);

        ItemEditDialogViewModel? previous = _openItemVm;
        _openItemVm = vm;
        previous?.CancelCommand.Execute(null);

        ItemEditResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<ItemEditDialogViewModel, ItemEditResult>(vm);
        }
        finally
        {
            if (ReferenceEquals(_openItemVm, vm)) _openItemVm = null;
        }
        if (result is null) return;

        // Defaults tier is read-only (MDB is source of truth) — fall back to Character.
        SettingsTier tier = result.Tier == SettingsTier.Defaults ? SettingsTier.Character : result.Tier;
        _resolver.WriteGameDataAt(tier, "Items", result.WccNoStr, result.Overlay);
    }
}
