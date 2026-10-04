using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Chest Offload window: open the containers you're holding (here or by typing
// `open <chest>`), diff a fresh inventory read before and after each open to show the
// coin the chest gave and every new item — grouped into the fewest shops with a charm
// picker and per-item sell quantities. Sell walks to the shop first when you aren't
// standing in it.
public sealed partial class ChestOffloadViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    public const int ContainerItemType = 8;

    // The one open Chest Offload window. Two would both read the inventory and diff
    // the same chest opens, so every way in raises this one instead.
    private static ChestOffloadViewModel? _openWindow;

    // Open the window, or bring it forward when it's already open — the Bosses tab
    // button and the Character Info panel's chest icon both come through here.
    public static async System.Threading.Tasks.Task OpenOrRaise()
    {
        DialogService dialogs = AppServices.Current.Dialogs;
        if (_openWindow is { } open && dialogs.RaiseIfOpen(open)) return;
        var vm = new ChestOffloadViewModel();
        _openWindow = vm;
        try { await dialogs.OpenWindowAsync<ChestOffloadViewModel, bool>(vm); }
        finally { if (ReferenceEquals(_openWindow, vm)) _openWindow = null; }
    }

    // Modeless browse/action window: it closes via the title-bar X, so this stays
    // unraised — it exists only to satisfy the DialogService contract.
    public event Action<bool>? CloseRequested;

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
    // Drops go out through the sweeps' pacer: a whole shop group of copies, one
    // `drop` per copy on Stock, would otherwise overflow the game's command queue.
    private readonly Action<IReadOnlyList<string>> _sendPaced;
    private readonly Action<RoomKey> _queueWalk;
    private readonly Action<RoomKey> _goWalk;
    private readonly AutoWalkManager? _walker;
    private readonly OutboundOpenObserver? _typedOpen;
    private readonly DispatcherTimer _reparse;
    // How long to wait for an open's before / after `i` before going on with the
    // cached inventory instead.
    private readonly DispatcherTimer _readTimeout;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly LogService? _log;
    private const string LogCategory = "ChestOffload";

    // Items and coin are attributed per open: each open is diffed against a fresh
    // inventory read taken just before it, so what was already carried, picked up
    // between opens, or paid for a sale never counts as chest loot.
    private readonly ChestLootLedger _ledger = new();
    private CurrencyHoldings _chestCoin = CurrencyHoldings.Empty;   // accumulated across opens

    // An open in flight: read the inventory, open, read it again.
    private enum OpenStep { Idle, ReadingBefore, AwaitingAfter }
    private OpenStep _openStep = OpenStep.Idle;
    private string _openTarget = "";
    private IReadOnlyList<string> _preOpenCarried = Array.Empty<string>();
    private CurrencyHoldings? _preOpenCurrency;                     // coin held just before the in-flight open
    private bool _settlePending;                                    // set once the forced 'i' is sent, awaiting the re-parse

    // A Sell that's walking to its shop first; it sells when the walk arrives.
    private sealed record PendingSell(RoomKey Room, int Shop, string ShopName, Action SellNow);
    private PendingSell? _pendingSell;
    private bool _simulating;
    private Dictionary<int, Room> _shopRoom = new();               // shop id → serving room (first found), rebuilt each render

    [ObservableProperty] private int _charm;
    [ObservableProperty] private string _sellTotal = "—";
    // What a Sell is doing when it isn't instant: walking to the shop, or why it stopped.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSellStatus))] private string _sellStatus = "";
    public bool HasSellStatus => SellStatus.Length > 0;
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
        AppServices.Current.GoWalkTo, AppServices.Current.Walker, AppServices.Current.OutboundOpen)
    { }

    public ChestOffloadViewModel(
        InventoryManager inventory, ShopStockIndex shops, RoomGraphManager rooms,
        ItemNameStore itemNames, PlayerStats stats, GameDataCache gameData,
        RoomTracker tracker, BfsMapper bfs, MovementFilter movement, CurrencyNaming naming,
        Action<string> send, Action<RoomKey> queueWalk,
        Action<IReadOnlyList<string>>? sendPaced = null,
        Action<RoomKey>? goWalk = null, AutoWalkManager? walker = null,
        OutboundOpenObserver? typedOpen = null)
    {
        _sendPaced = sendPaced ?? (cmds => { foreach (string cmd in cmds) send(cmd); });
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
        _goWalk = goWalk ?? queueWalk;
        _walker = walker;
        _typedOpen = typedOpen;

        _charm = stats.Charm > 0 ? stats.Charm : 50;

        RebuildContainers(_inventory.Snapshot);

        // Force an inventory re-read a beat after the loot spills, then diff. The
        // 'i' is what makes the chest's coin visible (chest give-lines carry no
        // coin), so the re-parse it triggers is the "after" snapshot for the open.
        _readTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _readTimeout.Tick += (_, _) => OnReadTimeout();
        _reparse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _reparse.Tick += (_, _) =>
        {
            _reparse.Stop();
            _settlePending = true;
            _send("i");
            _readTimeout.Stop();
            _readTimeout.Start();
        };
        _inventory.FullInventoryParsed += OnFullInventoryParsed;
        if (_walker is not null) _walker.Event += OnWalkerEvent;
        if (_typedOpen is not null) _typedOpen.OpenSent += OnTypedOpen;
        // Reconcile the list against the game's OWN confirmed sell/drop lines, so a
        // refused sale changes nothing and a partial one reduces only that item.
        _inventory.ItemSold += OnItemSold;
        _inventory.ItemDropped += OnItemDropped;

        // Test-only "Simulate Chest" button, revealed by the Log pane's "Simulate
        // Chest button" toggle (session-only, off by default). Mirror its live value.
        _diagnostics = AppServices.CurrentOrNull?.LogDiagnostics;
        if (_diagnostics is not null) _diagnostics.Changed += OnDiagnosticsChanged;
        _log = AppServices.CurrentOrNull?.Log;

        _log?.Info(LogCategory,
            $"window opened — charm {_charm}, {Containers.Count} container(s) held");
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
            if (_itemNames.FindByName(name) is int n && _itemNames.ItemTypeOf(n) == ContainerItemType)
                Containers.Add(new ChestContainerRow(name, count, () => OpenContainer(name)));
        }
        OnPropertyChanged(nameof(HasContainers));
    }

    // The Open button: read the inventory first, so the "before" is what's really
    // carried this moment rather than a cached copy, then open (OnFullInventoryParsed).
    private void OpenContainer(string name)
    {
        if (_openStep != OpenStep.Idle)
        {
            _log?.Info(LogCategory, $"open {name} ignored — {_openTarget} is still being opened");
            return;
        }
        _simulating = false;   // a real open returns to the live-inventory diff
        _openTarget = name;
        _openStep = OpenStep.ReadingBefore;
        _settlePending = false;
        _log?.Info(LogCategory, $"open {name} — reading inventory first for the before snapshot");
        _send("i");
        _readTimeout.Stop();
        _readTimeout.Start();
    }

    // `open <chest>` typed in the terminal. It's already on the wire, so there's no
    // reading the inventory first: the cached one is the before (kept current by the
    // gets and drops it tracks, and refreshed by the last open's after-read).
    private void OnTypedOpen(string target) => Dispatcher.UIThread.Post(() =>
    {
        if (_openStep != OpenStep.Idle) return;
        InventorySnapshot snap = _inventory.Snapshot;
        if (ChestOffloadPlanner.MatchContainer(target, Containers.Where(c => !c.Simulated).Select(c => c.Name))
            is not { } name)
            return;
        _simulating = false;
        _openTarget = name;
        _log?.Info(LogCategory, $"typed open {name} — using the cached inventory as the before snapshot");
        StartAfterRead(snap);
    });

    // An `i` that never parsed (cut off by combat output, say) mustn't leave the open
    // stuck: go on with the cached inventory, which the give lines keep current.
    private void OnReadTimeout()
    {
        _readTimeout.Stop();
        if (_openStep == OpenStep.ReadingBefore)
        {
            _log?.Info(LogCategory, $"no inventory read in {_readTimeout.Interval.TotalSeconds:0}s — opening {_openTarget} on the cached inventory");
            SendOpen(_inventory.Snapshot);
        }
        else if (_openStep == OpenStep.AwaitingAfter && _settlePending)
        {
            _log?.Info(LogCategory, $"no inventory read in {_readTimeout.Interval.TotalSeconds:0}s after opening {_openTarget} — diffing the cached inventory");
            RebuildLoot();
        }
    }

    private void SendOpen(InventorySnapshot before)
    {
        _readTimeout.Stop();
        _send($"open {_openTarget}");
        StartAfterRead(before);
    }

    // Hold the before snapshot and re-read the inventory once the loot has spilled.
    private void StartAfterRead(InventorySnapshot before)
    {
        _preOpenCarried = before.CarriedItems;
        _preOpenCurrency = before.Currency;
        _settlePending = false;                            // don't attribute until the forced 'i' lands
        _openStep = OpenStep.AwaitingAfter;
        _log?.Info(LogCategory, $"opened {_openTarget} — re-reading inventory in {_reparse.Interval.TotalMilliseconds:F0}ms for the loot + coin diff");
        _reparse.Stop();
        _reparse.Start();
    }

    // A full `i` landed: either the before-read of a button open (now send the open) or
    // the after-read of an open (now diff it). Only that after-read rebuilds the list —
    // sells and drops reconcile row by row (OnItemSold / OnItemDropped) so the user's
    // sell quantities and ⇄ shop moves survive. Simulated chests never rebuild from
    // live inventory.
    private void OnFullInventoryParsed() => Dispatcher.UIThread.Post(() =>
    {
        if (_simulating) return;
        if (_openStep == OpenStep.ReadingBefore) { SendOpen(_inventory.Snapshot); return; }
        if (_openStep == OpenStep.AwaitingAfter && _settlePending) RebuildLoot();
    });

    // The game confirmed a sale of `count` of `name` (the player's own "You sold …").
    // Reduce that row and drop it at zero, leaving every other row's edits intact.
    private void OnItemSold(string name, int count, long _)
        => Dispatcher.UIThread.Post(() => ReconcileConfirmed(name, count, sold: true));

    // The game confirmed a drop of `count` of `name` (the player's own "You dropped …").
    private void OnItemDropped(string name, int count)
        => Dispatcher.UIThread.Post(() => ReconcileConfirmed(name, count, sold: false));

    private void ReconcileConfirmed(string name, int count, bool sold)
    {
        if (_simulating || count <= 0) return;
        if (FindRow(name) is not (ChestOffloadShopGroup group, ChestOffloadItemRow row))
        {
            _log?.Debug(LogCategory,
                $"{(sold ? "sold" : "dropped")} {count} {name} — no matching offload row (already reconciled or not from a chest)");
            return;
        }

        int before = row.Gained;
        bool empty = sold ? row.ApplySold(count) : row.ApplyDropped(count);
        _log?.Info(LogCategory,
            $"{(sold ? "sold" : "dropped")} {count} {name} confirmed — held {before}→{row.Gained}" +
            (empty ? " (row cleared)" : $", sell qty now {row.SellQty}"));

        _ledger.Remove(name, count);
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

        // The before→after delta of THIS open is its coin and its items.
        if (_preOpenCurrency is { } pre)
        {
            CurrencyHoldings gain = CoinGain(pre, snap.Currency);
            _chestCoin = AddCoins(_chestCoin, gain);
            _log?.Info(LogCategory, $"open settled — coin this open +{gain.TotalCopperValue}c (accumulated {_chestCoin.TotalCopperValue}c)");
        }
        IReadOnlyList<(string Name, int Count)> opened = _ledger.AddOpen(_preOpenCarried, snap.CarriedItems);
        _preOpenCurrency = null;
        _settlePending = false;
        _openStep = OpenStep.Idle;
        _readTimeout.Stop();
        RebuildCoinGains(_chestCoin);

        IReadOnlyList<(string Name, int Count)> loot = _ledger.Current(snap.CarriedItems);
        _log?.Info(LogCategory,
            $"{_openTarget} gave {opened.Count} item type(s): " +
            (opened.Count == 0 ? "nothing" : string.Join(", ", opened.Select(g => $"{g.Count} {g.Name}"))) +
            $" — {loot.Count} item type(s) to offload");
        RenderLoot(loot);
    }

    // Positive per-denomination coin an open added (before → after). Clamped at
    // zero per denomination — an open only adds coin, never removes it.
    private static CurrencyHoldings CoinGain(CurrencyHoldings before, CurrencyHoldings now) => new(
        Math.Max(0, now.Copper - before.Copper),
        Math.Max(0, now.Silver - before.Silver),
        Math.Max(0, now.Gold - before.Gold),
        Math.Max(0, now.Platinum - before.Platinum),
        Math.Max(0, now.Runic - before.Runic),
        Math.Max(0, now.TotalCopperValue - before.TotalCopperValue));

    private static CurrencyHoldings AddCoins(CurrencyHoldings a, CurrencyHoldings b) => new(
        a.Copper + b.Copper, a.Silver + b.Silver, a.Gold + b.Gold,
        a.Platinum + b.Platinum, a.Runic + b.Runic, a.TotalCopperValue + b.TotalCopperValue);

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
            if (_itemNames.ItemTypeOf(number) == ContainerItemType) continue;   // don't sell chests
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
                    DropItem, BuildShopChoices, MoveItemToShop, SellItem));
            group.Reprice(Charm, _gameData.ActiveRealm);
            ShopGroups.Add(group);
        }

        Unsellable.Clear();
        foreach (LootItem li in noShop)
            Unsellable.Add(new ChestOffloadItemRow(li.Name, li.Count, li.BaseCopper, li.Shops, 0, null));

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

    private static string StepsText(Room? room, RoomKey? here, int? dist)
    {
        if (room is null || here is null) return "";
        if (dist is null) return "unreachable";
        return dist switch { 0 => "you're here", 1 => "1 step", _ => $"{dist} steps" };
    }

    partial void OnCharmChanged(int value)
    {
        foreach (ChestOffloadShopGroup group in ShopGroups) group.Reprice(value, _gameData.ActiveRealm);
        UpdateGrandTotal();
    }

    // Test aid: seed the container list with a handful of random real containers.
    // Clicking one still sends the real "open" (below), but its loot is rolled from
    // the chest's own table rather than waiting on a live drop, so the window can be
    // exercised without hunting chests. Re-clicking re-randomises the simulated set.
    private System.Collections.Generic.IReadOnlyDictionary<int, ChestContents>? _chestTables;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SimulateChest()
    {
        _simulating = true;   // hold the simulated view against real inventory refreshes
        _preOpenCurrency = null; _settlePending = false;   // drop any in-flight real open
        _openStep = OpenStep.Idle;
        _readTimeout.Stop();
        _chestCoin = CurrencyHoldings.Empty;               // fresh test run
        RebuildCoinGains(_chestCoin);
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
        _chestCoin = AddCoins(_chestCoin, RandomCoinGain());
        RebuildCoinGains(_chestCoin);

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

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    private void SellGroup(ChestOffloadShopGroup group)
    {
        _log?.Info(LogCategory, $"Sell All '{group.ShopName}' — {group.Items.Count} item(s)");
        SellAtShop(group.Shop, group.ShopName, () => SellNow(group.Items.ToList()));
    }

    private void SellItem(ChestOffloadItemRow item)
    {
        if (item.SellQty <= 0) return;
        string shopName = GroupOf(item)?.ShopName ?? $"Shop #{item.CurrentShop}";
        SellAtShop(item.CurrentShop, shopName, () => SellNow(new[] { item }));
    }

    // Sell here when we're standing in the shop; otherwise walk there (the full "Walk
    // here" path — conflicting engines stop, gated routes ask) and sell on arrival
    // (OnWalkerEvent). A new Sell replaces one still walking.
    private void SellAtShop(int shop, string shopName, Action sellNow)
    {
        if (_tracker.State.CurrentRoom?.Shop == shop)
        {
            CancelPendingSell(null);
            sellNow();
            return;
        }
        if (!_shopRoom.TryGetValue(shop, out Room? room))
        {
            SellStatus = $"{shopName}: no known room for this shop — walk there and sell by hand.";
            _log?.Info(LogCategory, $"sell at '{shopName}' skipped — no room serves shop #{shop}");
            return;
        }
        _pendingSell = new PendingSell(room.Key, shop, shopName, sellNow);
        SellStatus = $"Walking to {shopName} ({room.Key.Map}/{room.Key.Room}) to sell…";
        _log?.Info(LogCategory, $"walking to '{shopName}' {room.Key.Map}/{room.Key.Room} to sell there");
        _goWalk(room.Key);
    }

    // The walk a Sell started: sell when it arrives at the shop; stand down if it
    // stops, fails, or another walk replaces it.
    private void OnWalkerEvent(WalkEvent e) => Dispatcher.UIThread.Post(() =>
    {
        if (_pendingSell is not { } pending) return;
        bool ours = e.Destination is { } d && d.Equals(pending.Room);
        switch (e.Kind)
        {
            case WalkEventKind.Started when !ours:
                CancelPendingSell($"another walk started — not selling at {pending.ShopName}.");
                break;
            case WalkEventKind.Finished when ours:
                _pendingSell = null;
                if (_tracker.State.CurrentRoom?.Shop == pending.Shop)
                {
                    SellStatus = "";
                    _log?.Info(LogCategory, $"arrived at '{pending.ShopName}' — selling");
                    pending.SellNow();
                }
                else CancelPendingSell($"the walk ended outside {pending.ShopName} — nothing sold.");
                break;
            case WalkEventKind.Stopped or WalkEventKind.Failed when ours || e.Destination is null:
                CancelPendingSell($"walk to {pending.ShopName} {(e.Kind == WalkEventKind.Failed ? "failed" : "stopped")}" +
                    (e.Detail.Length > 0 ? $" ({e.Detail})" : "") + " — nothing sold.");
                break;
        }
    });

    private void CancelPendingSell(string? why)
    {
        _pendingSell = null;
        SellStatus = why ?? "";
        if (why is not null) _log?.Info(LogCategory, why);
    }

    // Send the picked quantity of each row, paced like the drops (Stock sells one copy
    // per command). The list is NOT touched here — it reconciles when the game's "You
    // sold …" lands, so a refused sale leaves the row untouched.
    private void SellNow(IReadOnlyList<ChestOffloadItemRow> items)
    {
        bool paradigm = _gameData.ActiveRealm == RealmType.ParaMud;
        List<string> sells = new();
        foreach (ChestOffloadItemRow item in items)
        {
            if (item.SellQty <= 0) continue;
            _log?.Info(LogCategory, $"sell {item.SellQty} {item.Name} (of {item.Gained} held)");
            CountedCommand.Emit(sells.Add, "sell", item.SellQty, item.Name, paradigm);
        }
        if (sells.Count > 0) _sendPaced(sells);
    }

    private ChestOffloadShopGroup? GroupOf(ChestOffloadItemRow item)
        => ShopGroups.FirstOrDefault(g => g.Items.Contains(item));

    // Drop this one item's whole held stack (the per-item counterpart to a shop's
    // Drop All, which drops every item's stack). The row is NOT removed here — it
    // reconciles when the game's "You dropped …" lands (ReconcileConfirmed), so a
    // blocked drop leaves the plan intact.
    private void DropItem(ChestOffloadItemRow item)
    {
        if (item.Gained <= 0) return;
        _log?.Info(LogCategory, $"drop {item.Gained} {item.Name} (whole stack)");
        List<string> drops = new();
        CountedCommand.Emit(drops.Add, "drop", item.Gained, item.Name, _gameData.ActiveRealm == RealmType.ParaMud);
        _sendPaced(drops);
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

    // Drop every item in this shop's listing — the whole held stack of each. The rows
    // are NOT removed here; each clears when its "You dropped …" confirms, so a blocked
    // drop stays visible.
    private void DropAllGroup(ChestOffloadShopGroup group)
    {
        _log?.Info(LogCategory, $"Drop All '{group.ShopName}' — {group.Items.Count} item(s)");
        bool paradigm = _gameData.ActiveRealm == RealmType.ParaMud;
        List<string> drops = new();
        foreach (ChestOffloadItemRow item in group.Items)
        {
            _log?.Info(LogCategory, $"drop {item.Gained} {item.Name} (whole stack)");
            CountedCommand.Emit(drops.Add, "drop", item.Gained, item.Name, paradigm);
        }
        _sendPaced(drops);
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
        _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        if (_walker is not null) _walker.Event -= OnWalkerEvent;
        if (_typedOpen is not null) _typedOpen.OpenSent -= OnTypedOpen;
        _readTimeout.Stop();
        _inventory.ItemSold -= OnItemSold;
        _inventory.ItemDropped -= OnItemDropped;
        if (_diagnostics is not null) _diagnostics.Changed -= OnDiagnosticsChanged;
        _reparse.Stop();
        _log?.Info(LogCategory, "window closed");
    }

    private readonly record struct LootItem(string Name, int Count, double BaseCopper, IReadOnlyCollection<int> Shops);
}
