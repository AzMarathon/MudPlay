using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Tracks every chest the player opens — from the Chest Offload tab's Open button
// or by typing `open <chest>` — whether or not the window is up. An open reads the
// inventory (`i`), opens, and reads it again; only what that one open added joins
// the list (ChestLootLedger), and — when the character has it switched on — its
// contents and coin are said to the room so everyone there knows what dropped. Chests opened back to back add up without
// double counting: each open's before is the read after the last one. A second Open
// click while one is in flight waits its turn; a second chest typed open mid-flight
// joins whichever diff will see its loot. The list is saved on the character profile and
// stays until the items are sold, dropped, hidden, or the player removes them, so closing
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
    // The container the Open button is opening (what `open` is sent for).
    private string _buttonTarget = "";
    // Every container whose loot the current before/after diff covers — the button's
    // one plus any typed open that landed inside the same diff.
    private readonly List<string> _opened = new();
    // Open clicks that came while another open was in flight: run in turn after it.
    private readonly Queue<string> _queued = new();
    // A typed open that landed while the button's before-read was still out: the
    // cached inventory at that moment, which can't contain either chest's loot yet.
    private InventorySnapshot? _beforeOverride;
    // Typed opens that landed after the after-read went out: their loot may show in
    // that read or only after it, so a follow-up diff starts from it.
    private readonly List<string> _followUp = new();
    private IReadOnlyList<string> _beforeCarried = Array.Empty<string>();
    private CurrencyHoldings _beforeCoin = CurrencyHoldings.Empty;
    private bool _afterReadSent;
    private int _generation;   // cancels a stale timer callback

    // The list or the coin changed as a whole (an open settled, a row removed, the
    // list cleared, a full inventory read pruned it, a profile loaded). Confirmed
    // sales, drops and hides update it quietly — the window reconciles its rows itself.
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
        _inventory.ItemHidden += OnItemHidden;
        _typedOpen.OpenSent += OnTypedOpen;
        _profile.ProfileLoaded += OnProfileLoaded;
        LoadFromProfile();
    }

    // Coin the chests on the list gave.
    public CurrencyHoldings Coin => _coin;

    public bool IsOpening => _step != Step.Idle;

    // Whether each open's contents are said to the room. Kept on the character
    // profile; off until the player ticks the window's checkbox.
    public bool SayLootToRoom
    {
        get => _profile.Current?.SayChestLootToRoom == true;
        set
        {
            if (_profile.Current is not { } prof || prof.SayChestLootToRoom == value) return;
            prof.SayChestLootToRoom = value;
            _log?.Info(LogCategory, $"say chest loot to the room: {(value ? "on" : "off")}");
            _profile.Save();
        }
    }

    // The list as it stands, each count capped at what's carried right now.
    public IReadOnlyList<(string Name, int Count)> Loot(IReadOnlyList<string> carried) => _ledger.Current(carried);

    // The window's Open button: read the inventory first, so the "before" is what's
    // really carried this moment rather than a cached copy, then open.
    public void Open(string name)
    {
        if (_step != Step.Idle)
        {
            _queued.Enqueue(name);
            _log?.Info(LogCategory, $"open {name} queued — {Label(_opened)} is still being opened");
            return;
        }
        _buttonTarget = name;
        _opened.Clear();
        _opened.Add(name);
        _beforeOverride = null;
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
    // inventory is the before (kept current by the gets and drops it tracks). One that
    // lands while another open is in flight joins whichever diff will see its loot.
    private void OnTypedOpen(string target) => _post(() =>
    {
        InventorySnapshot snap = _inventory.Snapshot;
        IEnumerable<string> containers = snap.CarriedItems
            .Select(t => CountedCommand.SplitLeadingCount(t).Name)
            .Where(_isContainer);
        if (ChestOffloadPlanner.MatchContainer(target, containers) is not { } name) return;
        switch (_step)
        {
            case Step.Idle:
                _opened.Clear();
                _opened.Add(name);
                _log?.Info(LogCategory, $"typed open {name} — using the cached inventory as the before snapshot");
                StartAfterRead(snap);
                break;
            case Step.ReadingBefore:
                // Its loot may beat the button's before-read, so the before has to be
                // what was carried before either chest opened.
                _beforeOverride ??= snap;
                _opened.Add(name);
                _log?.Info(LogCategory, $"typed open {name} while {_buttonTarget}'s inventory read was out — one diff covers both");
                break;
            case Step.AwaitingAfter when !_afterReadSent:
                _opened.Add(name);
                _log?.Info(LogCategory, $"typed open {name} before the after-read — counted with {Label(_opened)}");
                break;
            default:
                _followUp.Add(name);
                _log?.Info(LogCategory, $"typed open {name} after the after-read went out — a follow-up read will catch its loot");
                break;
        }
    });

    private static string Label(IReadOnlyList<string> names) => string.Join(" + ", names);

    private void OnReadTimeout(int gen)
    {
        if (gen != _generation) return;
        if (_step == Step.ReadingBefore)
        {
            _log?.Info(LogCategory, $"no inventory read in {ReadTimeoutMs / 1000}s — opening {_buttonTarget} on the cached inventory");
            SendOpen(_beforeOverride ?? _inventory.Snapshot);
        }
        else if (_step == Step.AwaitingAfter && _afterReadSent)
        {
            _log?.Info(LogCategory, $"no inventory read in {ReadTimeoutMs / 1000}s after opening {Label(_opened)} — diffing the cached inventory");
            Settle(_inventory.Snapshot);
        }
    }

    private void SendOpen(InventorySnapshot before)
    {
        _beforeOverride = null;
        _send($"open {_buttonTarget}");
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
        if (_step == Step.ReadingBefore) { SendOpen(_beforeOverride ?? snap); return; }
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
        string label = Label(_opened);
        _log?.Info(LogCategory,
            $"{label} gave " +
            (items.Count == 0 ? "no items" : string.Join(", ", items.Select(g => $"{g.Count} {g.Name}"))) +
            $" and {coin.TotalCopperValue}c");
        Announce(label, items, coin);
        SaveAndNotify();

        // A chest typed open after the after-read went out: diff again from this read.
        if (_followUp.Count > 0)
        {
            _opened.Clear();
            _opened.AddRange(_followUp);
            _followUp.Clear();
            StartAfterRead(after);
        }
        else if (_queued.Count > 0) Open(_queued.Dequeue());
    }

    private void Announce(string label, IReadOnlyList<(string Name, int Count)> items, CurrencyHoldings coin)
    {
        if (!SayLootToRoom) return;
        var coins = new List<string>();
        if (coin.Runic > 0) coins.Add($"{coin.Runic:N0} {_runicName()}");
        if (coin.Platinum > 0) coins.Add($"{coin.Platinum:N0} platinum");
        if (coin.Gold > 0) coins.Add($"{coin.Gold:N0} gold");
        if (coin.Silver > 0) coins.Add($"{coin.Silver:N0} silver");
        if (coin.Copper > 0) coins.Add($"{coin.Copper:N0} copper");
        foreach (string line in ChestOffloadPlanner.AnnounceLines(label, items, coins, AnnounceMaxChars))
            _send("." + line);
    }

    private void OnItemSold(string name, int count, long _) => _post(() => TakeOff(name, count));

    private void OnItemDropped(string name, int count) => _post(() => TakeOff(name, count));

    // A hidden item has left the pack as surely as a dropped one. The hide echo
    // comes through whole, so Paradigm's leading count is split off here.
    private void OnItemHidden(string item)
    {
        (int count, string name) = CountedCommand.SplitLeadingCount(item);
        _post(() => TakeOff(name, count));
    }

    private void TakeOff(string name, int count)
    {
        if (count <= 0 || !_ledger.Remove(name, count)) return;
        Save();
    }

    private void OnProfileLoaded(CharacterProfile _) => _post(() =>
    {
        ++_generation;
        _step = Step.Idle;
        _queued.Clear();
        _followUp.Clear();
        _beforeOverride = null;
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
        _inventory.ItemHidden -= OnItemHidden;
        _typedOpen.OpenSent -= OnTypedOpen;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
