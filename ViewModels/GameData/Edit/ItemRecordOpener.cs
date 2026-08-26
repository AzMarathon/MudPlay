using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Opens an item's record — the same ItemEditDialog the Game Data Browser's item
// double-click opens — from a bare item number + name, pulling the game-data
// pieces from AppServices.Current. Lets surfaces outside the browser (the Roomba
// Master List) open an item's record without duplicating ItemsSectionViewModel's
// table-row machinery; the dialog only needs the number, name, and MDB-derived
// panes, not a game-data row.
public static class ItemRecordOpener
{
    public static async Task OpenAsync(int itemNumber, string itemName)
    {
        if (itemNumber <= 0) return;
        AppServices s = AppServices.Current;
        string wcc = itemNumber.ToString(CultureInfo.InvariantCulture);

        // 4-tier merged overlay over the realm-flavoured seed baseline — matches
        // what the browser shows before any user override.
        ItemOverlay seedDefaults = s.ItemOverlaySeed.GetOverlay(itemNumber);
        ItemOverlay existing =
            s.Resolver.ResolveGameData<ItemOverlay>("Items", wcc, seedDefaults) ?? seedDefaults;

        GameDataCache cache = s.GameData;
        ItemMdbView mdb = new ItemMdbViewBuilder(cache, 50).Build(wcc);
        IReadOnlyList<ShopSaleRow> ShopsForCharm(int charm) =>
            new ItemMdbViewBuilder(cache, charm).Build(wcc).Shops;

        ChestContents? chest = ChestContentsReader.Read(cache, itemNumber);
        IReadOnlyList<ItemSource>? containerSources = s.ItemSources?.ContainersOf(itemNumber);
        IReadOnlyList<ItemGiver>? givers = s.ItemSources?.GiversOf(itemNumber);

        ItemEditDialogViewModel vm = new(
            wccNoStr:          wcc,
            mdbName:           itemName,
            existing:          existing,
            // No table row here to carry a source tier; default to the most
            // specific tier — the picker still lets the user retarget a save.
            currentTier:       SettingsTier.Character,
            mdbInfo:           mdb.OtherInfo,
            shops:             mdb.Shops,
            isLight:           mdb.IsLight,
            isContainer:       mdb.IsContainer,
            chest:             chest,
            containerSources:  containerSources,
            givers:            givers,
            shopSalesForCharm: ShopsForCharm,
            droppedBy:         mdb.DroppedBy,
            placedIn:          mdb.PlacedIn);

        await s.Dialogs.OpenWindowAsync<ItemEditDialogViewModel, ItemEditResult>(vm);
    }
}
