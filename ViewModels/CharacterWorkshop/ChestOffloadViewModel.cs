using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Chest Offload window: snapshot your carried inventory + coin when it opens, let
// you open the containers you're holding, then diff the inventory to show the coin
// the chest gave and every new item — grouped into the fewest shops with a charm
// picker and per-item sell quantities, and a per-shop walk + Sell.
public sealed partial class ChestOffloadViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    private const int ContainerItemType = 8;

    // Modeless browse/action window: it closes via the title-bar X, so this stays
    // unraised — it exists only to satisfy the DialogService contract.
    public event Action<bool>? CloseRequested;

    private readonly InventoryManager _inventory;
    private readonly ShopStockIndex _shops;
    private readonly RoomGraphManager _rooms;
    private readonly ItemNameStore _itemNames;
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly Action<string> _send;
    private readonly Action<RoomKey> _queueWalk;
    private readonly DispatcherTimer _reparse;

    private IReadOnlyList<string> _baselineCarried = Array.Empty<string>();
    private long _baselineCopper;

    [ObservableProperty] private int _charm;
    [ObservableProperty] private string _currencyGained = "—";
    public ObservableCollection<ChestContainerRow> Containers { get; } = new();
    public ObservableCollection<ChestOffloadShopGroup> ShopGroups { get; } = new();
    public ObservableCollection<ChestOffloadItemRow> Unsellable { get; } = new();

    public bool HasContainers => Containers.Count > 0;
    public bool HasLoot => ShopGroups.Count > 0 || Unsellable.Count > 0;
    public bool HasUnsellable => Unsellable.Count > 0;

    public ChestOffloadViewModel() : this(
        AppServices.Current.Inventory, AppServices.Current.ShopStock, AppServices.Current.RoomGraph,
        AppServices.Current.ItemNames, AppServices.Current.PlayerStats, AppServices.Current.GameData,
        cmd => AppServices.Current.SendGameCommand(cmd), AppServices.Current.QueueWalkTo)
    { }

    public ChestOffloadViewModel(
        InventoryManager inventory, ShopStockIndex shops, RoomGraphManager rooms,
        ItemNameStore itemNames, PlayerStats stats, GameDataCache gameData,
        Action<string> send, Action<RoomKey> queueWalk)
    {
        _inventory = inventory;
        _shops = shops;
        _rooms = rooms;
        _itemNames = itemNames;
        _stats = stats;
        _gameData = gameData;
        _send = send;
        _queueWalk = queueWalk;

        _charm = stats.Charm > 0 ? stats.Charm : 50;

        InventorySnapshot snap = _inventory.Snapshot;
        _baselineCarried = snap.CarriedItems;
        _baselineCopper = snap.Currency.TotalCopperValue;
        RebuildContainers(snap);

        // Force an inventory re-read a beat after the loot spills, then diff.
        _reparse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _reparse.Tick += (_, _) => { _reparse.Stop(); _send("i"); };
        _inventory.Changed += OnInventoryChanged;
    }

    private void RebuildContainers(InventorySnapshot snap)
    {
        Containers.Clear();
        foreach (string token in snap.CarriedItems)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(token);
            if (_itemNames.FindByName(name) is int n && _itemNames.ItemTypeOf(n) == ContainerItemType)
                Containers.Add(new ChestContainerRow(name, count, () => OpenContainer(name)));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    private void OpenContainer(string name)
    {
        _send($"open {name}");
        _reparse.Stop();
        _reparse.Start();
    }

    private void OnInventoryChanged() => Dispatcher.UIThread.Post(RebuildLoot);

    private void RebuildLoot()
    {
        InventorySnapshot snap = _inventory.Snapshot;
        long gained = snap.Currency.TotalCopperValue - _baselineCopper;
        CurrencyGained = gained > 0 ? ShopPriceCalculator.FormatCopper(gained) : "—";
        RebuildContainers(snap);

        // A shop can serve several rooms; v1 takes the first (nearest-shop routing
        // is a later refinement). Built once here on the UI thread.
        Dictionary<int, Room> shopRoom = new();
        foreach (Room room in _rooms.Rooms)
            if (room.Shop > 0 && !shopRoom.ContainsKey(room.Shop))
                shopRoom[room.Shop] = room;

        var loot = new List<LootItem>();
        foreach ((string name, int count) in ChestOffloadPlanner.CarriedGains(_baselineCarried, snap.CarriedItems))
        {
            if (_itemNames.FindByName(name) is not int number) continue;
            if (_itemNames.ItemTypeOf(number) == ContainerItemType) continue;   // don't sell chests
            loot.Add(new LootItem(name, count, BaseCopperOf(number), _shops.ShopsSelling(number)));
        }

        IReadOnlyList<(int Shop, IReadOnlyList<LootItem> Items)> groups =
            ChestOffloadPlanner.GroupByFewestShops(loot, li => li.Shops, out IReadOnlyList<LootItem> noShop);

        ShopGroups.Clear();
        foreach ((int shopNum, IReadOnlyList<LootItem> items) in groups)
        {
            shopRoom.TryGetValue(shopNum, out Room? room);
            var group = new ChestOffloadShopGroup(
                room?.Name ?? $"Shop #{shopNum}", room?.Key, _queueWalk, SellGroup);
            foreach (LootItem li in items)
                group.Items.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper, group.Retotal));
            group.Reprice(Charm, _gameData.ActiveRealm);
            ShopGroups.Add(group);
        }

        Unsellable.Clear();
        foreach (LootItem li in noShop)
            Unsellable.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper, null));

        OnPropertyChanged(nameof(HasLoot));
        OnPropertyChanged(nameof(HasUnsellable));
    }

    partial void OnCharmChanged(int value)
    {
        foreach (ChestOffloadShopGroup group in ShopGroups) group.Reprice(value, _gameData.ActiveRealm);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    private void SellGroup(ChestOffloadShopGroup group)
    {
        bool paradigm = _gameData.ActiveRealm == RealmType.ParaMud;
        foreach (ChestOffloadItemRow item in group.Items)
            CountedCommand.Emit(_send, "sell", item.SellQty, item.Name, paradigm);
    }

    private double BaseCopperOf(int number)
        => _gameData.FindRowByNumber("Items", number) is { } el
            ? ShopPriceCalculator.ToCopper(ReadInt(el, "Price"), ReadInt(el, "Currency"))
            : 0;

    private static int ReadInt(JsonElement el, string field)
        => el.TryGetProperty(field, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    public void Dispose()
    {
        _inventory.Changed -= OnInventoryChanged;
        _reparse.Stop();
    }

    private readonly record struct LootItem(string Name, int Count, double BaseCopper, IReadOnlyCollection<int> Shops);
}
