using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Record Keeping → Chest Offload: a view over ChestOpenTracker's list of chest loot
// (which lives on past the Workshop and is saved on the profile) — open the containers
// you're holding, see the coin and items the chests gave grouped into the fewest shops
// with a charm picker and per-item sell quantities, and sell (walking to the shop first
// when you aren't standing in it), drop, or take items off the list. A Drop is a
// discard, so it goes out as the auto-discard engine's would: hidden instead when
// Settings → Other "Hide items when discarding" is on.
public sealed partial class ChestOffloadViewModel : WorkshopSectionViewModel
{
    public const string SectionId = "chestoffload";
    public const string SectionTitle = "Chest Offload";
    public override string Id => SectionId;
    public override string Title => SectionTitle;

    private Control? _view;
    public override Control View => _view ??= new ChestOffloadSectionView { DataContext = this };

    // A column of shop groups reads best at a set width; left to fit its content
    // the window would stretch to the longest item line.
    public override Size? PreferredSize => new Size(660, 780);

    private readonly InventoryManager _inventory;
    private readonly ShopStockIndex _shops;
    private readonly RoomGraphManager _rooms;
    private readonly ItemNameStore _itemNames;
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly RoomTracker _tracker;
    private readonly BfsMapper _bfs;
    private readonly MovementFilter _movement;
    private readonly CurrencyNaming _naming;
    private readonly Action<string> _send;
    // Discards go out through the sweeps' pacer: a whole shop group of copies, one
    // `drop` or `hide` per copy on Stock, would otherwise overflow the game's
    // command queue.
    private readonly Action<IReadOnlyList<string>> _sendPaced;
    // Owns the hide-or-drop choice for a discard, and words the command.
    private readonly AutoDiscardManager _discard;
    private readonly Action<RoomKey> _queueWalk;
    private readonly ChestSellTour _tour;
    // Walking time for a number of steps (Auto-Lair's live travel model).
    private readonly Func<int, TimeSpan> _hopEta;
    private readonly ChestOpenTracker _chests;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly LogService? _log;
    private const string LogCategory = "ChestOffload";

    // Coin shown while the Simulate Chest test button drives the view.
    private CurrencyHoldings _simCoin = CurrencyHoldings.Empty;

    private bool _simulating;
    // The tab's own note on the status line: a refused hide (with the item it is
    // about, so it can come down when that hide is over) or the answer to the last
    // button press (no item; it comes down on the next press).
    private string _note = "";
    private string? _noteItem;
    private Dictionary<int, Room> _shopRoom = new();               // shop id → serving room (first found), rebuilt each render

    [ObservableProperty] private int _charm;
    [ObservableProperty] private string _sellTotal = "—";
    // What a Sell is doing when it isn't instant: walking to the shop, or why it
    // stopped. Also where a refused hide, or a Drop that sent nothing, is named.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSellStatus))] private string _sellStatus = "";
    public bool HasSellStatus => SellStatus.Length > 0;
    [ObservableProperty] private bool _isTourRunning;
    // Say each opened chest's contents to the room. The tracker keeps it on the profile.
    [ObservableProperty] private bool _sayLootToRoom;
    // Coin the chest gave, one denomination per line, most-expensive first.
    public ObservableCollection<string> CoinGains { get; } = new();
    public ObservableCollection<ChestContainerRow> Containers { get; } = new();
    public ObservableCollection<ChestOffloadShopGroup> ShopGroups { get; } = new();
    public ObservableCollection<ChestOffloadItemRow> Unsellable { get; } = new();

    public bool HasContainers => Containers.Count > 0;
    public bool HasCoinGain => CoinGains.Count > 0;
    public bool HasLoot => ShopGroups.Count > 0 || Unsellable.Count > 0;
    public bool HasUnsellable => Unsellable.Count > 0;

    public ChestOffloadViewModel() : this(
        AppServices.Current.Inventory, AppServices.Current.ShopStock, AppServices.Current.RoomGraph,
        AppServices.Current.ItemNames, AppServices.Current.PlayerStats, AppServices.Current.GameData,
        AppServices.Current.RoomTracker, AppServices.Current.Bfs, AppServices.Current.Movement,
        AppServices.Current.Currency,
        cmd => AppServices.Current.SendGameCommand(cmd), AppServices.Current.QueueWalkTo,
        cmds => AppServices.Current.InventoryAction.SendPaced(cmds),
        AppServices.Current.AutoDiscard,
        AppServices.Current.ChestOpens, AppServices.Current.ChestSellTour,
        hops => AppServices.Current.AutoLair.TravelCostModel.EstimateTravel(hops))
    { }

    public ChestOffloadViewModel(
        InventoryManager inventory, ShopStockIndex shops, RoomGraphManager rooms,
        ItemNameStore itemNames, PlayerStats stats, GameDataCache gameData,
        RoomTracker tracker, BfsMapper bfs, MovementFilter movement, CurrencyNaming naming,
        Action<string> send, Action<RoomKey> queueWalk,
        Action<IReadOnlyList<string>> sendPaced, AutoDiscardManager discard,
        ChestOpenTracker chests, ChestSellTour tour,
        Func<int, TimeSpan> hopEta)
    {
        _sendPaced = sendPaced;
        _discard = discard;
        _chests = chests;
        _inventory = inventory;
        _shops = shops;
        _rooms = rooms;
        _itemNames = itemNames;
        _stats = stats;
        _gameData = gameData;
        _tracker = tracker;
        _bfs = bfs;
        _movement = movement;
        _naming = naming;
        _send = send;
        _queueWalk = queueWalk;
        _tour = tour;
        _hopEta = hopEta;

        _charm = stats.Charm > 0 ? stats.Charm : 50;

        _chests.Changed += OnChestsChanged;
        _inventory.Changed += OnInventoryChanged;
        _tour.Changed += OnTourChanged;
        _sellStatus = _tour.Status;
        _isTourRunning = _tour.IsRunning;
        _sayLootToRoom = _chests.SayLootToRoom;
        // The tracker lowers the list on the game's OWN confirmed sell / drop / hide /
        // give lines, window open or not; the rows here follow it, so a refused sale
        // changes nothing and a partial one reduces only that item.
        _chests.ItemLeft += OnItemLeft;
        _discard.HideRefused += OnHideRefused;
        _discard.HeldHideEnded += OnHeldHideEnded;

        // Test-only "Simulate Chest" button, revealed by the Log pane's "Simulate
        // Chest button" toggle (session-only, off by default). Mirror its live value.
        _diagnostics = AppServices.CurrentOrNull?.LogDiagnostics;
        if (_diagnostics is not null) _diagnostics.Changed += OnDiagnosticsChanged;
        _log = AppServices.CurrentOrNull?.Log;

        RebuildLoot();
        _log?.Info(LogCategory,
            $"tab opened — charm {_charm}, {Containers.Count} container(s) held, " +
            $"{ShopGroups.Sum(g => g.Items.Count) + Unsellable.Count} item(s) on the list");
    }

    // Reveal the test-only "Simulate Chest" button — gated by the Log pane toggle,
    // so a normal user never sees it. Hidden by default (session-only + off).
    public bool ShowSimulateChest => _diagnostics?.ShowSimulateChest ?? false;

    private void OnDiagnosticsChanged()
        => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ShowSimulateChest)));

    private void RebuildContainers(InventorySnapshot snap)
    {
        Containers.Clear();
        foreach (string token in snap.CarriedItems)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(token);
            if (_itemNames.FindByName(name) is int n && _itemNames.ItemTypeOf(n) == ChestOffloadPlanner.ContainerItemType)
                Containers.Add(new ChestContainerRow(name, count, () => OpenContainer(name)));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    private void OpenContainer(string name)
    {
        _simulating = false;   // a real open returns to the tracker's list
        _chests.Open(name);
    }

    // The tracker's list changed as a whole (an open settled, a row was taken off, the
    // list was cleared, or its last item left and took the coin tally with it). A
    // sale, drop, hide or give that leaves items listed doesn't come through here —
    // it moves its one row (OnItemLeft) so the user's sell quantities and ⇄ shop
    // moves survive. Simulated chests keep their own view.
    private void OnChestsChanged()
    {
        SayLootToRoom = _chests.SayLootToRoom;   // a profile swap brings its own setting
        if (!_simulating) RebuildLoot();
    }

    partial void OnSayLootToRoomChanged(bool value) => _chests.SayLootToRoom = value;

    // The containers held can change any time (a chest picked up or opened).
    private void OnInventoryChanged() => Dispatcher.UIThread.Post(() => RebuildContainers(_inventory.Snapshot));

    // The game confirmed `count` of a listed item gone (the player's own "You sold …",
    // "You dropped …", "You hid …" or a give) and the tracker has lowered its list.
    // Reduce that row and drop it at zero, leaving every other row's edits intact.
    private void OnItemLeft(string name, int count, ChestOpenTracker.Departure how)
        => ReconcileConfirmed(name, count, how);

    // A full room refused a discard's hide. The row is left as it is (nothing
    // confirmed, so the item is still carried); AutoDiscardManager holds the copy
    // and sends the hide again in each new room until it lands. A running tour
    // keeps the status line for itself.
    private void OnHideRefused(string name) => Dispatcher.UIThread.Post(() =>
    {
        if (_simulating || FindRow(name) is null) return;
        _log?.Info(LogCategory, $"hide of {name} refused (no room to hide it here) — row kept, tried again in the next room");
        if (_tour.IsRunning) return;
        ShowNote($"No room to hide {name} here — it stays in your pack and is tried again in the next room you enter.", name);
    });

    // The held hide is over, landed or called off: its note no longer holds.
    private void OnHeldHideEnded(string name) => Dispatcher.UIThread.Post(() =>
    {
        if (string.Equals(_noteItem, name, StringComparison.OrdinalIgnoreCase)) ClearNote();
    });

    private void ShowNote(string text, string? heldItem = null)
    {
        _note = text;
        _noteItem = heldItem;
        SellStatus = text;
    }

    // Take the tab's note down, unless something else has taken the line since.
    private void ClearNote()
    {
        if (_note.Length > 0 && SellStatus == _note) SellStatus = "";
        _note = "";
        _noteItem = null;
    }

    // A button was pressed: what the last press was told is stale. A refused-hide
    // note stays, since its hide is still waiting.
    private void ClearActionNote()
    {
        if (_noteItem is null) ClearNote();
    }

    // The player has other plans for these items (a sale, off the list): a hide of
    // theirs still waiting for a room would take the copy first, or take one of
    // the player's own once the chest's is gone.
    private void CallOffHeldHides(IEnumerable<string> names)
    {
        foreach (string name in names) _discard.ReleaseHeld(name, int.MaxValue, byHandOnly: true);
    }

    private void ReconcileConfirmed(string name, int count, ChestOpenTracker.Departure how)
    {
        if (_simulating || count <= 0) return;
        string did = how.ToString().ToLowerInvariant();
        if (FindRow(name) is not (ChestOffloadShopGroup group, ChestOffloadItemRow row))
        {
            // No shop buys it, so it sits in the list below the shops.
            ChestOffloadItemRow? unsold = Unsellable.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (unsold is null) _log?.Debug(LogCategory, $"{did} {count} {name} — no row to move");
            else if (unsold.ApplyDiscarded(count)) RemoveRow(unsold);
            return;
        }

        int before = row.Gained;
        bool empty = how == ChestOpenTracker.Departure.Sold ? row.ApplySold(count) : row.ApplyDiscarded(count);
        // The tracker has logged the confirmation; this is the row's side of it.
        _log?.Debug(LogCategory,
            $"{did} {count} {name} — row held {before}→{row.Gained}" +
            (empty ? " (row cleared)" : $", sell qty now {row.SellQty}"));

        if (empty)
        {
            group.Items.Remove(row);
            if (group.Items.Count == 0) ShopGroups.Remove(group);
        }
        group.Retotal();
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
    }

    // Locate the sellable row for an item by name (chest-loot rows only; the
    // unsellable list carries no sell/drop buttons). First match wins — an item maps
    // to a single shop group at a time.
    private (ChestOffloadShopGroup Group, ChestOffloadItemRow Row)? FindRow(string name)
    {
        foreach (ChestOffloadShopGroup g in ShopGroups)
            foreach (ChestOffloadItemRow r in g.Items)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    return (g, r);
        return null;
    }

    private void RebuildLoot()
    {
        InventorySnapshot snap = _inventory.Snapshot;
        RebuildContainers(snap);
        RebuildCoinGains(_chests.Coin);
        RenderLoot(_chests.Loot(snap.CarriedItems));
    }

    // Turn a set of (item name, count) gains into the priced, shop-grouped view.
    // Shared by the real inventory diff and the "Simulate Chest" test button.
    private void RenderLoot(IReadOnlyList<(string Name, int Count)> gains)
    {

        // A shop can serve several rooms; take the first (kept as a field so the
        // "sell elsewhere" popup and item moves can look shops up later). UI thread.
        _shopRoom = new Dictionary<int, Room>();
        foreach (Room room in _rooms.Rooms)
            if (room.Shop > 0 && !_shopRoom.ContainsKey(room.Shop))
                _shopRoom[room.Shop] = room;

        var loot = new List<LootItem>();
        foreach ((string name, int count) in gains)
        {
            if (_itemNames.FindByName(name) is not int number) continue;
            if (_itemNames.ItemTypeOf(number) == ChestOffloadPlanner.ContainerItemType) continue;   // don't sell chests
            loot.Add(new LootItem(name, count, BaseCopperOf(number), _shops.ShopsSelling(number)));
        }

        IReadOnlyList<(int Shop, IReadOnlyList<LootItem> Items)> groups =
            ChestOffloadPlanner.GroupByFewestShops(loot, li => li.Shops, out IReadOnlyList<LootItem> noShop);

        ShopGroups.Clear();
        foreach ((int shopNum, IReadOnlyList<LootItem> items) in OrderByRoute(groups, _shopRoom))
        {
            _shopRoom.TryGetValue(shopNum, out Room? room);
            string location = room is { } lr ? $"{lr.Key.Map}/{lr.Key.Room}" : "";
            var group = new ChestOffloadShopGroup(
                room?.Name ?? $"Shop #{shopNum}", shopNum, location, StepsLabel(room), room?.Key,
                _queueWalk, SellGroup, DropAllGroup);
            foreach (LootItem li in items)
                group.Items.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper,
                    li.Shops, shopNum,
                    it => { GroupOf(it)?.Retotal(); UpdateGrandTotal(); },
                    DropItem, BuildShopChoices, MoveItemToShop, SellItem, RemoveItem));
            group.Reprice(Charm, _gameData.ActiveRealm);
            ShopGroups.Add(group);
        }

        Unsellable.Clear();
        foreach (LootItem li in noShop)
            Unsellable.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper, li.Shops, 0, null,
                onRemove: RemoveItem));

        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
        OnPropertyChanged(nameof(HasUnsellable));
    }

    // Coin the chest gave as a vertical list, most-expensive denomination first,
    // skipping any that came up empty. Shorthand names; the runic tier uses the
    // active board's word (some realms rename it).
    private void RebuildCoinGains(CurrencyHoldings g)
    {
        CoinGains.Clear();
        if (g.Runic > 0)    CoinGains.Add($"{g.Runic:N0} {_naming.RunicName}");
        if (g.Platinum > 0) CoinGains.Add($"{g.Platinum:N0} plat");
        if (g.Gold > 0)     CoinGains.Add($"{g.Gold:N0} gold");
        if (g.Silver > 0)   CoinGains.Add($"{g.Silver:N0} silver");
        if (g.Copper > 0)   CoinGains.Add($"{g.Copper:N0} copper");
        OnPropertyChanged(nameof(HasCoinGain));
    }

    // Grand total: everything selected across every shop at the current charm.
    private void UpdateGrandTotal()
    {
        long total = 0;
        foreach (ChestOffloadShopGroup group in ShopGroups)
            foreach (ChestOffloadItemRow item in group.Items)
                total += item.LineCopper;
        SellTotal = total > 0 ? ShopPriceCalculator.FormatCopper(total) : "—";
    }

    // Order the shop groups into a short trip: the shop nearest the player's current
    // room first, then the nearest not-yet-visited shop from there (a nearest-
    // neighbour route). Distances use the same routing the walker does —
    // Bfs.DistanceBetween under the MovementFilter — so they honour avoid rooms,
    // usable teleport gates, and item/hazard/boat gates (rope & grapple, ferries…)
    // rather than raw hops. A room-less or currently-unroutable shop sorts last; no
    // known current room (offline / unknown position) leaves the grouping order.
    private IReadOnlyList<(int Shop, IReadOnlyList<LootItem> Items)> OrderByRoute(
        IReadOnlyList<(int Shop, IReadOnlyList<LootItem> Items)> groups, Dictionary<int, Room> shopRoom)
    {
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        if (here is null || groups.Count <= 1) return groups;

        var remaining = groups.ToList();
        var ordered = new List<(int, IReadOnlyList<LootItem>)>(remaining.Count);
        RoomKey pos = here.Value;
        while (remaining.Count > 0)
        {
            int bestIdx = 0, bestDist = int.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                int d = shopRoom.TryGetValue(remaining[i].Shop, out Room? r)
                        && _bfs.DistanceBetween(pos, r.Key, _movement) is { } dd
                    ? dd : int.MaxValue;
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            (int Shop, IReadOnlyList<LootItem> Items) chosen = remaining[bestIdx];
            ordered.Add(chosen);
            remaining.RemoveAt(bestIdx);
            if (shopRoom.TryGetValue(chosen.Shop, out Room? cr)) pos = cr.Key;
        }
        return ordered;
    }

    // Steps from the player's current room to a shop, under the walker's routing
    // (same filter as OrderByRoute). Empty when we don't know where we are or the
    // shop has no room; "unreachable" when no route survives the filter.
    private string StepsLabel(Room? room)
    {
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        int? dist = room is { } r && here is { } h ? _bfs.DistanceBetween(h, r.Key, _movement) : null;
        return StepsText(room, here, dist);
    }

    private string StepsText(Room? room, RoomKey? here, int? dist)
    {
        if (room is null || here is null) return "";
        if (dist is not { } steps) return "unreachable";
        return steps == 0 ? "you're here" : StepsAndEta(steps);
    }

    // "12 steps · ~45s": the steps plus their walking time under the same per-step
    // travel model the Navigation status ETA uses. Fights on the way aren't counted.
    private string StepsAndEta(int steps)
    {
        string count = steps == 1 ? "1 step" : $"{steps} steps";
        TimeSpan eta = _hopEta(steps);
        return eta > TimeSpan.Zero ? $"{count} · ~{RouteEtaEstimator.FormatCompact(eta)}" : count;
    }

    partial void OnCharmChanged(int value)
    {
        foreach (ChestOffloadShopGroup group in ShopGroups) group.Reprice(value, _gameData.ActiveRealm);
        UpdateGrandTotal();
    }

    // Test aid: seed the container list with a handful of random real containers.
    // Clicking one still sends the real "open" (below), but its loot is rolled from
    // the chest's own table rather than waiting on a live drop, so the tab can be
    // exercised without hunting chests. Re-clicking re-randomises the simulated set.
    private System.Collections.Generic.IReadOnlyDictionary<int, ChestContents>? _chestTables;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SimulateChest()
    {
        _simulating = true;   // hold the simulated view against the tracker's list
        _simCoin = CurrencyHoldings.Empty;                 // fresh test run
        RebuildCoinGains(_simCoin);
        _chestTables ??= ChestContentsReader.ReadAll(_gameData);
        for (int i = Containers.Count - 1; i >= 0; i--)
            if (Containers[i].Simulated) Containers.RemoveAt(i);

        foreach (int id in _chestTables.Keys.OrderBy(_ => Random.Shared.Next()).Take(4))
        {
            if (_itemNames.GetName(id) is not { } name) continue;
            int number = id;
            Containers.Add(new ChestContainerRow(name, 1, () => SimulateOpen(name, number), simulated: true));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    // Send the real "open", then roll this chest's loot table: pick an item count in
    // its [MinItems, MaxItems] range, then draw that many items weighted by each
    // drop's chance (with replacement, so a chest can hand out multiples). Renders
    // the rolled loot through the same shop-grouping pipeline as a real open.
    private void SimulateOpen(string name, int number)
    {
        _simulating = true;
        _send($"open {name}");

        // No real currency change to diff in simulation, so add a believable spill.
        CurrencyHoldings spill = RandomCoinGain();
        _simCoin = new CurrencyHoldings(
            _simCoin.Copper + spill.Copper, _simCoin.Silver + spill.Silver, _simCoin.Gold + spill.Gold,
            _simCoin.Platinum + spill.Platinum, _simCoin.Runic + spill.Runic,
            _simCoin.TotalCopperValue + spill.TotalCopperValue);
        RebuildCoinGains(_simCoin);

        if (_chestTables is null || !_chestTables.TryGetValue(number, out ChestContents? contents)
            || contents.Drops.Count == 0)
        {
            RenderLoot(Array.Empty<(string, int)>());
            return;
        }

        int count = Random.Shared.Next(contents.MinItems, contents.MaxItems + 1);
        double totalWeight = contents.Drops.Sum(d => d.Probability);
        var rolled = new Dictionary<string, int>();
        for (int i = 0; i < count && totalWeight > 0; i++)
        {
            double r = Random.Shared.NextDouble() * totalWeight;
            foreach (ChestDrop drop in contents.Drops)
            {
                r -= drop.Probability;
                if (r <= 0) { rolled[drop.ItemName] = rolled.GetValueOrDefault(drop.ItemName) + 1; break; }
            }
        }
        RenderLoot(rolled.Select(kv => (kv.Key, kv.Value)).ToList());
    }

    // A believable per-denomination coin spill for the Simulate Chest test path.
    private static CurrencyHoldings RandomCoinGain()
    {
        int copper = Random.Shared.Next(0, 200);
        int silver = Random.Shared.Next(0, 80);
        int gold = Random.Shared.Next(0, 40);
        int plat = Random.Shared.Next(0, 12);
        int runic = Random.Shared.Next(0, 2);
        long total = copper + silver * 10L + gold * 100L + plat * 10_000L + runic * 1_000_000L;
        return new CurrencyHoldings(copper, silver, gold, plat, runic, total);
    }

    // Take one item off the list (it stays in the pack). The tracker's Changed
    // rebuilds the view.
    private void RemoveItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (_simulating) { RemoveRow(item); return; }
        CallOffHeldHides(new[] { item.Name });
        _chests.RemoveItem(item.Name);
    }

    // Empty the whole list and the coin tally.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearList()
    {
        ClearActionNote();
        if (_simulating)
        {
            _simulating = false;
            _simCoin = CurrencyHoldings.Empty;
        }
        else
        {
            CallOffHeldHides(ShopGroups.SelectMany(g => g.Items).Select(r => r.Name));
        }
        _chests.Clear();
        RebuildLoot();
    }

    // A simulated row has nothing in the tracker behind it — just drop it from view.
    private void RemoveRow(ChestOffloadItemRow item)
    {
        if (GroupOf(item) is { } group)
        {
            group.Items.Remove(item);
            if (group.Items.Count == 0) ShopGroups.Remove(group);
            else group.Retotal();
        }
        else Unsellable.Remove(item);
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
        OnPropertyChanged(nameof(HasUnsellable));
    }

    // Sell All: a one-stop tour of this shop (sells here when standing in it).
    private void SellGroup(ChestOffloadShopGroup group)
    {
        ClearActionNote();
        _log?.Info(LogCategory, $"Sell All '{group.ShopName}' — {group.Items.Count} item(s)");
        if (StopFor(group.Shop, group.ShopName, group.Items) is { } stop) StartTour(new[] { stop });
    }

    private void SellItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (item.SellQty <= 0) return;
        string shopName = GroupOf(item)?.ShopName ?? $"Shop #{item.CurrentShop}";
        if (StopFor(item.CurrentShop, shopName, new[] { item }) is { } stop) StartTour(new[] { stop });
    }

    // A sale walks rooms before it sells, and a held hide goes out in the first new
    // room: it would hide the item on the way to the shop.
    private void StartTour(IReadOnlyList<ChestSellTour.Stop> stops)
    {
        CallOffHeldHides(stops.SelectMany(s => s.Items).Select(i => i.Name));
        _tour.Start(stops);
    }

    // Sell Tour: read the inventory fresh (so the list's counts are the truth), show
    // exactly what each shop will be sold, and on Yes walk and sell them all in route
    // order. Quantities are capped at what the chests gave and are still carried, here
    // for the prompt and again by the tour right before each sell.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async System.Threading.Tasks.Task SellTour()
    {
        ClearActionNote();
        if (_tour.IsRunning) { SellStatus = "A sell tour is already running — cancel it first."; return; }
        if (_simulating) { SellStatus = "Simulated chests can't be sold — Clear list first."; return; }
        await ReadInventoryAsync();

        var stops = new List<ChestSellTour.Stop>();
        foreach (ChestOffloadShopGroup group in ShopGroups)
            if (StopFor(group.Shop, group.ShopName, group.Items) is { } stop) stops.Add(stop);
        if (stops.Count == 0) { SellStatus = "Nothing on the list to sell."; return; }

        // Each leg's steps and walking time from the stop before it (the first from
        // where you stand), so a long walk for one cheap item is plain before you go.
        var body = new System.Text.StringBuilder();
        body.Append("Walk to each shop below in this order and sell exactly these items:\n");
        int n = 0, totalSteps = 0;
        bool anyUnknown = false;
        RoomKey? from = _tracker.State.CurrentRoom?.Key;
        foreach (ChestSellTour.Stop stop in stops)
        {
            int? leg = from is { } f ? _bfs.DistanceBetween(f, stop.Room, _movement) : null;
            string legText = leg switch
            {
                null => "steps unknown",
                0 => "you're here",
                _ => StepsAndEta(leg.Value),
            };
            if (leg is { } l) totalSteps += l; else anyUnknown = true;
            body.Append($"\n{++n}. {stop.ShopName} ({stop.Room.Map}/{stop.Room.Room}) — {legText}, sells for {ValueOf(stop)}\n   ")
                .Append(string.Join(", ", stop.Items.Select(i => $"{i.Qty} {i.Name}")))
                .Append('\n');
            from = stop.Room;
        }
        body.Append($"\nWhole tour: {(anyUnknown ? "at least " : "")}{StepsAndEta(totalSteps)} of walking (fights on the way not counted).\n");
        body.Append("\nOnly items the chests gave are sold, never more than the chests gave — " +
                    "anything you already had stays in your pack. A running loop or Auto-Lair stops for the walk.");
        bool go = await AppServices.Current.Confirm.ConfirmAsync("Sell Tour", body.ToString(), "Start tour");
        if (!go) return;
        StartTour(stops);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CancelTour() => _tour.Cancel();

    // What a stop's items fetch at the current charm: each row's line price, scaled to
    // the (possibly capped) quantity the stop sells.
    private string ValueOf(ChestSellTour.Stop stop)
    {
        double copper = 0;
        foreach ((string name, int qty) in stop.Items)
            if (FindRow(name) is (_, ChestOffloadItemRow row) && row.SellQty > 0)
                copper += (double)row.LineCopper * qty / row.SellQty;
        return copper > 0 ? ShopPriceCalculator.FormatCopper((long)Math.Round(copper)) : "—";
    }

    // One shop's stop: each row's picked quantity, capped at what the chests gave and
    // are still carried. Null when nothing is left to sell there, or the shop has no
    // known room to walk to.
    private ChestSellTour.Stop? StopFor(int shop, string shopName, IEnumerable<ChestOffloadItemRow> rows)
    {
        IReadOnlyList<(string Name, int Count)> loot = _chests.Loot(_inventory.Snapshot.CarriedItems);
        var items = new List<(string Name, int Qty)>();
        foreach (ChestOffloadItemRow row in rows)
        {
            int fromChests = loot.FirstOrDefault(l => string.Equals(l.Name, row.Name, StringComparison.OrdinalIgnoreCase)).Count;
            int qty = Math.Min(row.SellQty, fromChests);
            if (qty > 0) items.Add((row.Name, qty));
        }
        if (items.Count == 0) return null;
        if (!_shopRoom.TryGetValue(shop, out Room? room))
        {
            SellStatus = $"{shopName}: no known room for this shop — walk there and sell by hand.";
            _log?.Info(LogCategory, $"sell at '{shopName}' skipped — no room serves shop #{shop}");
            return null;
        }
        return new ChestSellTour.Stop(room.Key, shop, shopName, items);
    }

    // Send `i` and wait for it to parse (or 3 s), so the tracker has pruned the list to
    // what's really carried before quantities are worked out.
    private async System.Threading.Tasks.Task ReadInventoryAsync()
    {
        var parsed = new System.Threading.Tasks.TaskCompletionSource();
        void OnParsed() => parsed.TrySetResult();
        _inventory.FullInventoryParsed += OnParsed;
        try
        {
            _send("i");
            await System.Threading.Tasks.Task.WhenAny(parsed.Task, System.Threading.Tasks.Task.Delay(3000));
            // The tracker prunes on the same parse through a UI-thread post; let it run.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { _inventory.FullInventoryParsed -= OnParsed; }
    }

    private void OnTourChanged() => Dispatcher.UIThread.Post(() =>
    {
        SellStatus = _tour.Status;
        IsTourRunning = _tour.IsRunning;
    });

    private ChestOffloadShopGroup? GroupOf(ChestOffloadItemRow item)
        => ShopGroups.FirstOrDefault(g => g.Items.Contains(item));

    // Discard this one item's whole held stack (the per-item counterpart to a shop's
    // Drop All, which discards every item's stack). The row is NOT removed here — it
    // reconciles when the game's "You dropped …" / "You hid …" lands
    // (ReconcileConfirmed), so a blocked discard leaves the plan intact.
    private void DropItem(ChestOffloadItemRow item)
    {
        ClearActionNote();
        if (item.Gained <= 0 || RefuseSimulatedDiscard()) return;
        List<string> commands = new();
        QueueDiscard(commands, item);
        _sendPaced(commands);
    }

    // A simulated row stands for nothing in the pack, and a hide for an item that
    // isn't held is not refused by the game but spoken to the room.
    private bool RefuseSimulatedDiscard()
    {
        if (!_simulating) return false;
        ShowNote("Simulated chests can't be dropped — Clear list first.");
        return true;
    }

    // Word one row's discard, hide or drop as the auto-discard engine would send
    // it, and log which it was. Only copies still carried and not already on their
    // way go out, so a second press before the game answers the first sends nothing.
    private void QueueDiscard(List<string> commands, ChestOffloadItemRow item)
    {
        (string verb, int sent, int held, string? blocked) = _discard.EmitDiscard(commands.Add, item.Name, item.Gained);
        if (sent > 0)
            _log?.Info(LogCategory, $"discard {sent} {item.Name} via {verb}" +
                (sent < item.Gained ? $" (of {item.Gained}; the rest held, already sent or not carried)" : " (whole stack)"));
        if (held > 0)
        {
            // A hide that could get no answer where the character stands (the dark,
            // blind, the game busy with a menu), or one auto-discard had waiting.
            _log?.Info(LogCategory, $"hide of {held} {item.Name} held: it goes out when a room can take it");
            if (!_tour.IsRunning)
                ShowNote($"{item.Name}: can't be hidden here right now — it stays in your pack and is hidden as soon as it can be.", item.Name);
            return;
        }
        if (sent > 0) return;

        // Until an `i` has been read the pack is unknown, and nothing is sent for a
        // copy that can't be shown to be carried.
        bool waiting = blocked is null && _inventory.IsLoaded && _discard.HeldFor(item.Name) > 0;
        string why = blocked
            ?? (!_inventory.IsLoaded ? "your inventory hasn't been read yet (type i)"
            : waiting ? "it is waiting for the next room to be hidden"
            : "it was already sent, or is no longer in your pack");
        _log?.Info(LogCategory, $"discard of {item.Name} not sent: {why}");
        // A waiting hide's note comes down with the hide; any other on the next press.
        if (!_tour.IsRunning) ShowNote($"{item.Name}: nothing sent — {why}.", waiting ? item.Name : null);
    }

    // Alternate shops that also buy this item, nearest first, minus the one it's in.
    private IReadOnlyList<ShopChoiceRow> BuildShopChoices(ChestOffloadItemRow item)
    {
        RoomKey? here = _tracker.State.CurrentRoom?.Key;
        var scored = new List<(int? Dist, ShopChoiceRow Row)>();
        foreach (int shop in item.CandidateShops)
        {
            if (shop == item.CurrentShop) continue;
            _shopRoom.TryGetValue(shop, out Room? room);
            int? dist = room is { } r && here is { } h ? _bfs.DistanceBetween(h, r.Key, _movement) : null;
            string location = room is { } lr ? $"{lr.Key.Map}/{lr.Key.Room}" : "";
            scored.Add((dist, new ShopChoiceRow(shop, room?.Name ?? $"Shop #{shop}", location,
                StepsText(room, here, dist))));
        }
        return scored
            .OrderBy(t => t.Dist is null)          // reachable first
            .ThenBy(t => t.Dist ?? int.MaxValue)   // then nearest
            .Select(t => t.Row)
            .ToList();
    }

    // Move an item to another shop it can be sold at: pull it from its current group
    // (dropping the group if that empties it), and drop it into the target group,
    // creating that group if this is the first item headed there.
    private void MoveItemToShop(ChestOffloadItemRow item, int targetShop)
    {
        if (targetShop == item.CurrentShop) return;

        ChestOffloadShopGroup? source = GroupOf(item);
        source?.Items.Remove(item);

        ChestOffloadShopGroup? target = ShopGroups.FirstOrDefault(g => g.Shop == targetShop);
        if (target is null)
        {
            _shopRoom.TryGetValue(targetShop, out Room? room);
            string location = room is { } lr ? $"{lr.Key.Map}/{lr.Key.Room}" : "";
            target = new ChestOffloadShopGroup(room?.Name ?? $"Shop #{targetShop}", targetShop, location,
                StepsLabel(room), room?.Key, _queueWalk, SellGroup, DropAllGroup);
            ShopGroups.Add(target);
        }

        item.CurrentShop = targetShop;
        target.Items.Add(item);

        if (source is not null && source.Items.Count == 0) ShopGroups.Remove(source);
        else source?.Retotal();
        target.Retotal();
        UpdateGrandTotal();
        OnPropertyChanged(nameof(HasLoot));
    }

    // Discard every item in this shop's listing — the whole held stack of each. The
    // rows are NOT removed here; each clears when its "You dropped …" / "You hid …"
    // confirms, so a blocked discard stays visible.
    private void DropAllGroup(ChestOffloadShopGroup group)
    {
        ClearActionNote();
        if (RefuseSimulatedDiscard()) return;
        _log?.Info(LogCategory, $"Drop All '{group.ShopName}' — {group.Items.Count} item(s)");
        List<string> commands = new();
        foreach (ChestOffloadItemRow item in group.Items) QueueDiscard(commands, item);
        _sendPaced(commands);
    }

    private double BaseCopperOf(int number)
        => _gameData.FindRowByNumber("Items", number) is { } el
            ? ShopPriceCalculator.ToCopper(ReadInt(el, "Price"), ReadInt(el, "Currency"))
            : 0;

    private static int ReadInt(JsonElement el, string field)
        => el.TryGetProperty(field, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : 0;

    public override void Dispose()
    {
        _chests.Changed -= OnChestsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _tour.Changed -= OnTourChanged;
        _chests.ItemLeft -= OnItemLeft;
        _discard.HideRefused -= OnHideRefused;
        _discard.HeldHideEnded -= OnHeldHideEnded;
        if (_diagnostics is not null) _diagnostics.Changed -= OnDiagnosticsChanged;
        _log?.Info(LogCategory, "tab closed");
    }

    private readonly record struct LootItem(string Name, int Count, double BaseCopper, IReadOnlyCollection<int> Shops);
}
