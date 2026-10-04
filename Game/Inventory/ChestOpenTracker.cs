using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Tracks every chest the player opens — from the Chest Offload window's Open button
// or by typing `open <chest>` — whether or not the window is up. An open reads the
// inventory (`i`), opens, and reads it again; only what that one open added joins
// the list (ChestLootLedger), and its contents and coin are said to the room so
// everyone there knows what dropped. The list is saved on the character profile and
// stays until the items are sold, dropped, or the player removes them, so closing
// the window (or the client) never loses it. UI thread only: line and inventory
// events arrive through `post`.
public sealed class ChestOpenTracker : IDisposable
{
    public const string LogCategory = "ChestOffload";

    // The loot spills a beat after the open; the after-read waits this long.
    private const int SettleMs = 900;
    // An `i` that never parses (cut off by combat output, say) mustn't strand an open.
    private const int ReadTimeoutMs = 3000;
    // Same conservative per-line budget the client uses for other chat lines.
    private const int AnnounceMaxChars = 120;

    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly OutboundOpenObserver _typedOpen;
    private readonly Func<string, bool> _isContainer;
    private readonly Action<string> _send;
    private readonly Action<int, Action> _schedule;
    private readonly Action<Action> _post;
    private readonly Func<string> _runicName;
    private readonly LogService? _log;

    private readonly ChestLootLedger _ledger = new();
    private CurrencyHoldings _coin = CurrencyHoldings.Empty;

    private enum Step { Idle, ReadingBefore, AwaitingAfter }
    private Step _step = Step.Idle;
    private string _target = "";
    private IReadOnlyList<string> _beforeCarried = Array.Empty<string>();
    private CurrencyHoldings _beforeCoin = CurrencyHoldings.Empty;
    private bool _afterReadSent;
    private int _generation;   // cancels a stale timer callback

    // The list or the coin changed as a whole (an open settled, a row removed, the
    // list cleared, a full inventory read pruned it, a profile loaded). Confirmed
    // sales and drops update it quietly — the window reconciles its rows itself.
    public event Action? Changed;

    public ChestOpenTracker(
        InventoryManager inventory, ProfileService profile, OutboundOpenObserver typedOpen,
        Func<string, bool> isContainer, Action<string> send, Action<int, Action> schedule,
        Action<Action> post, Func<string> runicName, LogService? log = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _typedOpen = typedOpen ?? throw new ArgumentNullException(nameof(typedOpen));
        _isContainer = isContainer ?? throw new ArgumentNullException(nameof(isContainer));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _runicName = runicName ?? throw new ArgumentNullException(nameof(runicName));
        _log = log;

        _inventory.FullInventoryParsed += OnFullInventoryParsed;
        _inventory.ItemSold += OnItemSold;
        _inventory.ItemDropped += OnItemDropped;
        _typedOpen.OpenSent += OnTypedOpen;
        _profile.ProfileLoaded += OnProfileLoaded;
        LoadFromProfile();
    }

    // Coin the chests on the list gave.
    public CurrencyHoldings Coin => _coin;

    public bool IsOpening => _step != Step.Idle;

    // The list as it stands, each count capped at what's carried right now.
    public IReadOnlyList<(string Name, int Count)> Loot(IReadOnlyList<string> carried) => _ledger.Current(carried);

    // The window's Open button: read the inventory first, so the "before" is what's
    // really carried this moment rather than a cached copy, then open.
    public void Open(string name)
    {
        if (_step != Step.Idle)
        {
            _log?.Info(LogCategory, $"open {name} ignored — {_target} is still being opened");
            return;
        }
        _target = name;
        _step = Step.ReadingBefore;
        _log?.Info(LogCategory, $"open {name} — reading inventory first for the before snapshot");
        _send("i");
        int gen = ++_generation;
        _schedule(ReadTimeoutMs, () => OnReadTimeout(gen));
    }

    // The player took this item off the list; it stays in the pack.
    public void RemoveItem(string name)
    {
        if (!_ledger.RemoveAll(name)) return;
        _log?.Info(LogCategory, $"{name} removed from the list by hand");
        SaveAndNotify();
    }

    public void Clear()
    {
        _ledger.Clear();
        _coin = CurrencyHoldings.Empty;
        _log?.Info(LogCategory, "list cleared by hand");
        SaveAndNotify();
    }

    // `open <chest>` typed in the terminal. It's already on the wire, so the cached
    // inventory is the before (kept current by the gets and drops it tracks).
    private void OnTypedOpen(string target) => _post(() =>
    {
        if (_step != Step.Idle) return;
        InventorySnapshot snap = _inventory.Snapshot;
        IEnumerable<string> containers = snap.CarriedItems
            .Select(t => CountedCommand.SplitLeadingCount(t).Name)
            .Where(_isContainer);
        if (ChestOffloadPlanner.MatchContainer(target, containers) is not { } name) return;
        _target = name;
        _log?.Info(LogCategory, $"typed open {name} — using the cached inventory as the before snapshot");
        StartAfterRead(snap);
    });

    private void OnReadTimeout(int gen)
    {
        if (gen != _generation) return;
        if (_step == Step.ReadingBefore)
        {
            _log?.Info(LogCategory, $"no inventory read in {ReadTimeoutMs / 1000}s — opening {_target} on the cached inventory");
            SendOpen(_inventory.Snapshot);
        }
        else if (_step == Step.AwaitingAfter && _afterReadSent)
        {
            _log?.Info(LogCategory, $"no inventory read in {ReadTimeoutMs / 1000}s after opening {_target} — diffing the cached inventory");
            Settle(_inventory.Snapshot);
        }
    }

    private void SendOpen(InventorySnapshot before)
    {
        _send($"open {_target}");
        StartAfterRead(before);
    }

    // Hold the before snapshot and re-read the inventory once the loot has spilled.
    // The 'i' is also what shows the chest's coin (the give lines carry none).
    private void StartAfterRead(InventorySnapshot before)
    {
        _beforeCarried = before.CarriedItems;
        _beforeCoin = before.Currency;
        _afterReadSent = false;
        _step = Step.AwaitingAfter;
        int gen = ++_generation;
        _schedule(SettleMs, () =>
        {
            if (gen != _generation) return;
            _afterReadSent = true;
            _send("i");
            int readGen = ++_generation;
            _schedule(ReadTimeoutMs, () => OnReadTimeout(readGen));
        });
    }

    private void OnFullInventoryParsed() => _post(() =>
    {
        InventorySnapshot snap = _inventory.Snapshot;
        if (_step == Step.ReadingBefore) { SendOpen(snap); return; }
        if (_step == Step.AwaitingAfter)
        {
            if (_afterReadSent) Settle(snap);
            return;
        }
        if (_ledger.Prune(snap.CarriedItems))
        {
            _log?.Info(LogCategory, "list trimmed to what the inventory shows carried");
            SaveAndNotify();
        }
    });

    // The before→after delta of THIS open is its coin and its items.
    private void Settle(InventorySnapshot after)
    {
        ++_generation;
        _step = Step.Idle;
        CurrencyHoldings coin = CoinGain(_beforeCoin, after.Currency);
        IReadOnlyList<(string Name, int Count)> items = _ledger.AddOpen(_beforeCarried, after.CarriedItems);
        _coin = AddCoins(_coin, coin);
        _ledger.Prune(after.CarriedItems);
        _log?.Info(LogCategory,
            $"{_target} gave " +
            (items.Count == 0 ? "no items" : string.Join(", ", items.Select(g => $"{g.Count} {g.Name}"))) +
            $" and {coin.TotalCopperValue}c");
        Announce(items, coin);
        SaveAndNotify();
    }

    private void Announce(IReadOnlyList<(string Name, int Count)> items, CurrencyHoldings coin)
    {
        var coins = new List<string>();
        if (coin.Runic > 0) coins.Add($"{coin.Runic:N0} {_runicName()}");
        if (coin.Platinum > 0) coins.Add($"{coin.Platinum:N0} platinum");
        if (coin.Gold > 0) coins.Add($"{coin.Gold:N0} gold");
        if (coin.Silver > 0) coins.Add($"{coin.Silver:N0} silver");
        if (coin.Copper > 0) coins.Add($"{coin.Copper:N0} copper");
        foreach (string line in ChestOffloadPlanner.AnnounceLines(_target, items, coins, AnnounceMaxChars))
            _send("." + line);
    }

    private void OnItemSold(string name, int count, long _) => _post(() => TakeOff(name, count));

    private void OnItemDropped(string name, int count) => _post(() => TakeOff(name, count));

    private void TakeOff(string name, int count)
    {
        if (count <= 0 || !_ledger.Remove(name, count)) return;
        Save();
    }

    private void OnProfileLoaded(CharacterProfile _) => _post(() =>
    {
        ++_generation;
        _step = Step.Idle;
        LoadFromProfile();
        Changed?.Invoke();
    });

    private void LoadFromProfile()
    {
        ChestLootRecord? saved = _profile.Current?.ChestLoot;
        _ledger.Load(saved?.Items.Select(e => (e.Name, e.Count)) ?? Enumerable.Empty<(string, int)>());
        _coin = saved is null
            ? CurrencyHoldings.Empty
            : new CurrencyHoldings(saved.Copper, saved.Silver, saved.Gold, saved.Platinum, saved.Runic, saved.TotalCopper);
    }

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_profile.Current is not { } prof) return;
        prof.ChestLoot = _ledger.IsEmpty && _coin.TotalCopperValue == 0
            ? null
            : new ChestLootRecord
            {
                Items = _ledger.Entries.Select(e => new ChestLootEntry(e.Name, e.Count)).ToList(),
                Copper = _coin.Copper, Silver = _coin.Silver, Gold = _coin.Gold,
                Platinum = _coin.Platinum, Runic = _coin.Runic, TotalCopper = _coin.TotalCopperValue,
            };
        _profile.Save();
    }

    // Positive per-denomination coin an open added. Clamped at zero per denomination —
    // an open only adds coin, never removes it.
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

    public void Dispose()
    {
        _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        _inventory.ItemSold -= OnItemSold;
        _inventory.ItemDropped -= OnItemDropped;
        _typedOpen.OpenSent -= OnTypedOpen;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
