using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// The Items tab's edit dialog offers "Installed defaults" as a Use option (the
// reset target) and hands back the record its boxes describe, which
// GameDataOverrideApplier.ApplyItemAsync saves (ItemOverrideSaveTests).
public sealed class ItemEditDialogViewModelTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoInfo =
        Array.Empty<KeyValuePair<string, string>>();
    private static readonly IReadOnlyList<ShopSaleRow> NoShops = Array.Empty<ShopSaleRow>();

    private static ItemEditResult Save(ItemEditDialogViewModel vm)
    {
        ItemEditResult? captured = null;
        vm.CloseRequested += r => captured = r;
        vm.SaveCommand.Execute(null);
        Assert.NotNull(captured);
        return captured!;
    }

    private static ItemEditDialogViewModel MakeVm(
        ItemOverlay? existing, SettingsTier currentTier = SettingsTier.Character)
        => new(
            wccNoStr: "1", mdbName: "torch", existing: existing,
            currentTier: currentTier, mdbInfo: NoInfo, shops: NoShops,
            writableTiers: [SettingsTier.Character, SettingsTier.Global]);

    [Fact]
    public void Picker_OffersInstalledDefaults_ButDefaultsToWritableTier()
    {
        ItemEditDialogViewModel vm = MakeVm(new ItemOverlay(), currentTier: SettingsTier.Defaults);
        Assert.Contains(SettingsTier.Defaults, vm.AvailableTiers);
        Assert.Equal(SettingsTier.Character, vm.UseTier);
    }

    // A box left clear and the item's own name are unset in the record, the shape
    // of an untouched installed default.
    [Fact]
    public void Save_LeavesAClearedBox_AndTheItemsOwnName_Unset()
    {
        ItemEditDialogViewModel vm = MakeVm(new ItemOverlay { AutoStash = true });
        vm.AutoStash = false;

        ItemOverlay saved = Save(vm).Overlay;

        Assert.Equal(new ItemOverlay(), saved);
    }

    [Fact]
    public void Save_KeepsARename()
    {
        ItemEditDialogViewModel vm = MakeVm(null);
        vm.Name = "old torch";

        Assert.Equal("old torch", Save(vm).Overlay.Name);
    }

    // Sell detours: the flag, the detour count and the ticked "Sell here" shops round-
    // trip, and a charm re-price keeps the ticks.
    [Fact]
    public void SellDetour_RoundTripsFlagCountAndShopPicks()
    {
        IReadOnlyList<ShopSaleRow> Rows() => new[]
        {
            new ShopSaleRow("Shop A - 1/10", "", 1, 10),
            new ShopSaleRow("Shop B - 2/20", "", 2, 20),
        };
        ItemEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "dagger",
            existing: new ItemOverlay { AutoSell = true, SellDetour = true, SellDetourAbove = "5", SellShops = "2/20" },
            currentTier: SettingsTier.Character, mdbInfo: NoInfo, shops: Rows(),
            shopSalesForCharm: _ => Rows());

        Assert.False(vm.ShopSales[0].SellHere);
        Assert.True(vm.ShopSales[1].SellHere);

        vm.ShopSales[0].SellHere = true;
        vm.Charm = 80;                               // rebuilds the rows
        Assert.True(vm.ShopSales[0].SellHere);

        ItemOverlay saved = Save(vm).Overlay;
        Assert.True(saved.SellDetour);
        Assert.Equal("5", saved.SellDetourAbove);
        Assert.Equal("1/10,2/20", saved.SellShops);
    }

    // A blank detour count means no detour, so ticking detours with it blank shows the
    // red nudge; any number, 0 included, clears it.
    [Fact]
    public void SellDetour_BlankCount_ShowsTheWarning()
    {
        ItemEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "dagger",
            existing: new ItemOverlay { AutoSell = true, SellDetour = true },
            currentTier: SettingsTier.Character, mdbInfo: NoInfo,
            shops: new[] { new ShopSaleRow("Shop A - 1/10", "", 1, 10) },
            shopSalesForCharm: _ => new[] { new ShopSaleRow("Shop A - 1/10", "", 1, 10) });
        Assert.True(vm.NeedsSellDetourAbove);

        vm.SellDetourAbove = "0";
        Assert.False(vm.NeedsSellDetourAbove);

        vm.SellDetourAbove = "";
        vm.SellDetour = false;
        Assert.False(vm.NeedsSellDetourAbove);
    }

    // A detour needs Auto-sell: unticking Auto-sell clears the detour and its count,
    // and an item saved that way before is cleaned up on its next save (report
    // paradigm-20260930-183614).
    [Fact]
    public void UntickingAutoSell_ClearsTheSellDetour()
    {
        ItemEditDialogViewModel vm = MakeVm(
            new ItemOverlay { AutoSell = true, SellDetour = true, SellDetourAbove = "5" });
        vm.AutoSell = false;
        Assert.False(vm.SellDetour);
        Assert.Equal(string.Empty, vm.SellDetourAbove);

        ItemOverlay stale = Save(MakeVm(new ItemOverlay { SellDetour = true, SellDetourAbove = "5" })).Overlay;
        Assert.Null(stale.SellDetour);
        Assert.Null(stale.SellDetourAbove);
    }

    // A real export's room-command items came up with every coffin room under Given
    // by and again under Room command. With a Room command group on the item, Given
    // by leaves every room out, listed below or not, and keeps the NPCs.
    [Fact]
    public void Givers_WithARoomCommandGroup_KeepOnlyNpcs()
    {
        RoomCommandRow pry = new(new[] { "pry coffin" }, "1.5%", new[]
        {
            new PlacedInRow("Tomb - 17/278", 17, 278),
        });
        ItemGiver[] givers =
        {
            new(ItemGiverKind.Room, 0, 17, 278, "Tomb", "", "14", false),
            new(ItemGiverKind.Room, 0, 17, 1792, "Library", "", "search shelves", true),
            new(ItemGiverKind.Monster, 278, 0, 0, "old hermit", "", "key", true),
        };

        IReadOnlyList<ItemGiverRow> rows = ItemEditDialogViewModel.BuildGivers(givers, new[] { pry }, (_, _) => false);

        Assert.Equal(new[] { "old hermit" }, rows.Select(r => r.Name));
    }

    // An export without room commands has no such group: Given by is whole.
    [Fact]
    public void Givers_WithNoRoomCommandGroup_AreAllShown()
    {
        ItemGiver[] givers =
        {
            new(ItemGiverKind.Room, 0, 17, 278, "Tomb", "", "14", false),
            new(ItemGiverKind.Monster, 278, 0, 0, "old hermit", "", "key", true),
        };

        IReadOnlyList<ItemGiverRow> rows =
            ItemEditDialogViewModel.BuildGivers(givers, Array.Empty<RoomCommandRow>(), (_, _) => false);

        Assert.Equal(new[] { "Tomb", "old hermit" }, rows.Select(r => r.Name));
    }

    // A room the user blacklisted is off the map and out of room search, and is left
    // out of Given by too.
    [Fact]
    public void Givers_LeaveOutABlacklistedRoom()
    {
        ItemGiver[] givers =
        {
            new(ItemGiverKind.Room, 0, 17, 278, "Tomb", "", "14", false),
            new(ItemGiverKind.Room, 0, 8, 461, "Statue Room", "", "touch statue", true),
            new(ItemGiverKind.Monster, 278, 0, 0, "old hermit", "", "key", true),
        };

        IReadOnlyList<ItemGiverRow> rows = ItemEditDialogViewModel.BuildGivers(
            givers, Array.Empty<RoomCommandRow>(), (map, room) => map == 17 && room == 278);

        Assert.Equal(new[] { "Statue Room", "old hermit" }, rows.Select(r => r.Name));
    }

    private static ItemEditDialogViewModel MakeVmWithExitUses(IReadOnlyList<PlacedInRow>? exitUses)
        => new(
            wccNoStr: "1", mdbName: "jail key", existing: null,
            currentTier: SettingsTier.Character, mdbInfo: NoInfo, shops: NoShops,
            exitUses: exitUses);

    // An item no exit names shows no Used on section; a key shows one line per exit.
    [Fact]
    public void ExitUses_ShowOnlyWhenSomeExitNamesTheItem()
    {
        Assert.False(MakeVmWithExitUses(null).HasExitUses);
        Assert.False(MakeVmWithExitUses(Array.Empty<PlacedInRow>()).HasExitUses);

        ItemEditDialogViewModel key = MakeVmWithExitUses(new[]
        {
            new PlacedInRow("Cell - 1/10 - south (Key)", 1, 10),
            new PlacedInRow("Hall - 1/11 - north (Key)", 1, 11),
        });

        Assert.True(key.HasExitUses);
        Assert.Equal(new[] { "Cell - 1/10 - south (Key)", "Hall - 1/11 - north (Key)" },
                     key.ExitUses!.ShownRooms.Select(r => r.Location));
        Assert.False(key.ExitUses.CanExpand);
    }

    // A key with many uses opens on the first six, with a count to show the rest.
    [Fact]
    public void ExitUses_ALongList_IsCappedWithACount()
    {
        PlacedInRow[] many = Enumerable.Range(1, 8)
            .Select(i => new PlacedInRow($"Room {i} - 1/{i} - north (Item)", 1, i)).ToArray();
        ItemEditDialogViewModel vm = MakeVmWithExitUses(many);

        Assert.Equal(6, vm.ExitUses!.ShownRooms.Count);
        Assert.True(vm.ExitUses.CanExpand);
        Assert.Equal("Show all 8 uses", vm.ExitUses.ToggleLabel);

        vm.ExitUses.ToggleCommand.Execute(null);

        Assert.Equal(8, vm.ExitUses.ShownRooms.Count);
        Assert.Equal("Show fewer", vm.ExitUses.ToggleLabel);
    }
}
