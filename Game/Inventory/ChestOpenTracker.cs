using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Tracks every chest that gets opened — from the Chest Offload tab's Open button,
// by typing `open <chest>`, or by the auto-open engine (AutoOpenManager, through
// TryOpenNow) — whether or not the window is up. An open reads the
// inventory (`i`), opens, and reads it again; only what that one open added joins
// the list (ChestLootLedger), and — when the character has it switched on — its
// contents and coin are said to the room so everyone there knows what dropped. Chests opened back to back add up without
// double counting: each open's before is the read after the last one. A second Open
// click while one is in flight waits its turn; a second chest typed open mid-flight
// joins whichever diff will see its loot. The list is saved on the character profile and
// stays until the items are sold, dropped, hidden, given away, or the player removes
// them, so closing the window (or the client) never loses it. The list is lowered
// here, on the game's own confirmations, and the window follows from ItemLeft: it
// is right whether or not the window is up. UI thread only: line and inventory
// events arrive through `post`.
public sealed class ChestOpenTracker : IDisposable
{
    public const string LogCategory = "ChestOffload";

    // How a listed item left the pack. A sale is told apart because the window takes
    // the copies sold out of the ones picked to sell.
    public enum Departure { Sold, Dropped, Hidden, Given }

    // One read open: the containers it covered, and the items and coin they gave.
    public sealed record OpenResult(
        IReadOnlyList<string> Opened, IReadOnlyList<(string Name, int Count)> Items, CurrencyHoldings Coin);

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
    // The pack as of the last change seen, to tell what the next one moved.
    private InventorySnapshot _lastSeen;
    // The read after the open, held from the moment it parses until it is diffed.
    private InventorySnapshot? _afterRead;
    private bool _afterReadSent;
    private int _generation;   // cancels a stale timer callback
    // True while one of our own `open` commands is going out. Every send, an
    // engine's as much as a typed line, passes the outbound observers, so without
    // this the tracker would hear its own open as one the player typed and count
    // the container a second time.
    private bool _sendingOpen;

    // The list or the coin changed as a whole (an open settled, a row removed, the
    // list cleared or emptied, a full inventory read pruned it, a profile loaded).
    public event Action? Changed;

    // A listed item left the pack by the game's own confirmation, and the list is
    // already lowered: the item, how many, and how. The window moves its row from
    // this, keeping the sell quantities and shop moves a rebuild would lose.
    public event Action<string, int, Departure>? ItemLeft;

    // An open has been read and the tracker has moved on: what it gave.
    public event Action<OpenResult>? OpenSettled;

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

        _lastSeen = _inventory.Snapshot;
        _inventory.Changed += OnInventoryChanged;
        _inventory.FullInventoryParsed += OnFullInventoryParsed;
        _inventory.ItemSold += OnItemSold;
        _inventory.ItemDropped += OnItemDropped;
        _inventory.ItemHidden += OnItemHidden;
        _inventory.ItemGivenAway += OnItemGivenAway;
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

    // Open a container with no read first, for a caller acting on the pack change
    // that brought it in (the auto-open engine): the cached inventory already is the
    // before. False, with nothing sent, while another open is in flight; the caller
    // asks again on OpenSettled.
    public bool TryOpenNow(string name)
    {
        if (_step != Step.Idle) return false;
        _opened.Clear();
        _opened.Add(name);
        _log?.Info(LogCategory, $"auto-open {name} — using the cached inventory as the before snapshot");
        InventorySnapshot before = _inventory.Snapshot;
        SendOwnOpen(name);
        StartAfterRead(before);
        return true;
    }

    // The player took this item off the list; it stays in the pack.
    public void RemoveItem(string name)
    {
        bool hadItems = !_ledger.IsEmpty;
        if (!_ledger.RemoveAll(name)) return;
        _log?.Info(LogCategory, $"{name} removed from the list by hand");
        DropCoinIfEmptied(hadItems);
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
    private void OnTypedOpen(string target)
    {
        if (_sendingOpen) return;
        // A direction is a door: the game looks for one before it looks in the pack
        // (GAME_MECHANICS "Chests and chest loot tables"), so `open s` at a south
        // door is not the small sack being opened — whoever sent it.
        if (DirectionExtensions.TryFromToken(target, out _)) return;
        _post(() => TrackTypedOpen(target));
    }

    private void TrackTypedOpen(string target)
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
    }

    private void SendOwnOpen(string name)
    {
        _sendingOpen = true;
        try { _send($"open {name}"); }
        finally { _sendingOpen = false; }
    }

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
        SendOwnOpen(_buttonTarget);
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

    // Between an open and the read after it, whatever the pack is seen to gain or
    // lose line by line — a pickup, a sale, a piece put on — is not the chest's
    // doing: a chest's contents only show in the full read. The before moves with
    // each such change, so the read's surplus is the chest's alone. An auto-opened
    // chest arrives with the rest of a kill's loot still being picked up; held
    // still, the before would list all of that as the chest's. A full read raises
    // Changed too, while the manager is still capturing it: that one is a read, not
    // a step, and moves nothing.
    private void OnInventoryChanged()
    {
        InventorySnapshot now = _inventory.Snapshot;
        if (_step == Step.AwaitingAfter && _afterRead is null && !_inventory.IsCapturing)
            MoveBefore(_lastSeen, now);
        _lastSeen = now;
    }

    private void MoveBefore(InventorySnapshot was, InventorySnapshot now)
    {
        Dictionary<string, int> had = ChestOffloadPlanner.CountByName(was.CarriedItems);
        Dictionary<string, int> has = ChestOffloadPlanner.CountByName(now.CarriedItems);
        Dictionary<string, int> before = ChestOffloadPlanner.CountByName(_beforeCarried);
        foreach (string name in had.Keys.Union(has.Keys, StringComparer.OrdinalIgnoreCase))
        {
            int moved = has.GetValueOrDefault(name) - had.GetValueOrDefault(name);
            if (moved != 0) before[name] = before.GetValueOrDefault(name) + moved;
        }
        _beforeCarried = before.Where(kv => kv.Value > 0)
            .Select(kv => kv.Value > 1 ? $"{kv.Value} {kv.Key}" : kv.Key).ToList();

        CurrencyHoldings b = _beforeCoin, w = was.Currency, n = now.Currency;
        _beforeCoin = new CurrencyHoldings(
            Math.Max(0, b.Copper + n.Copper - w.Copper),
            Math.Max(0, b.Silver + n.Silver - w.Silver),
            Math.Max(0, b.Gold + n.Gold - w.Gold),
            Math.Max(0, b.Platinum + n.Platinum - w.Platinum),
            Math.Max(0, b.Runic + n.Runic - w.Runic),
            Math.Max(0, b.TotalCopperValue + n.TotalCopperValue - w.TotalCopperValue));
    }

    private void OnFullInventoryParsed()
    {
        // The after-read is taken as it parses: by the time the posted half runs,
        // lines behind it in the same burst may have moved the pack again.
        if (_step == Step.AwaitingAfter && _afterReadSent) _afterRead ??= _inventory.Snapshot;
        _post(() =>
        {
            if (_step == Step.ReadingBefore) { SendOpen(_beforeOverride ?? _inventory.Snapshot); return; }
            if (_step == Step.AwaitingAfter)
            {
                if (_afterRead is { } after) Settle(after);
                return;
            }
            bool hadItems = !_ledger.IsEmpty;
            if (_ledger.Prune(_inventory.Snapshot.CarriedItems))
            {
                _log?.Info(LogCategory, "list trimmed to what the inventory shows carried");
                DropCoinIfEmptied(hadItems);
                SaveAndNotify();
            }
        });
    }

    // The before→after delta of THIS open is its coin and its items.
    private void Settle(InventorySnapshot after)
    {
        ++_generation;
        _step = Step.Idle;
        _afterRead = null;
        // What was listed and has since left the pack goes first: a list this read
        // finds empty takes its coin with it, and the open starts a new one.
        bool hadItems = !_ledger.IsEmpty;
        if (_ledger.Prune(after.CarriedItems)) DropCoinIfEmptied(hadItems);
        CurrencyHoldings coin = CoinGain(_beforeCoin, after.Currency);
        IReadOnlyList<(string Name, int Count)> items = _ledger.AddOpen(_beforeCarried, after.CarriedItems);
        _coin = AddCoins(_coin, coin);
        _ledger.Prune(after.CarriedItems);
        var result = new OpenResult(_opened.ToList(), items, coin);
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

        OpenSettled?.Invoke(result);
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

    // Every way the game confirms an item gone, whoever sent the command: a line
    // typed in the terminal, the window's buttons, the auto-sell and auto-discard
    // engines, a stash.
    private void OnItemSold(string name, int count, long _) => _post(() => TakeOff(name, count, Departure.Sold));

    private void OnItemDropped(string name, int count) => _post(() => TakeOff(name, count, Departure.Dropped));

    // A hidden item has left the pack as surely as a dropped one. The hide echo
    // comes through whole, so Paradigm's leading count is split off here.
    private void OnItemHidden(string item)
    {
        (int count, string name) = CountedCommand.SplitLeadingCount(item);
        _post(() => TakeOff(name, count, Departure.Hidden));
    }

    private void OnItemGivenAway(string name, int count, string _) => _post(() => TakeOff(name, count, Departure.Given));

    private void TakeOff(string name, int count, Departure how)
    {
        if (count <= 0) return;
        bool hadItems = !_ledger.IsEmpty;
        if (!_ledger.Remove(name, count)) return;
        _log?.Info(LogCategory, $"{how.ToString().ToLowerInvariant()} {count} {name} confirmed — taken off the list");
        bool emptied = DropCoinIfEmptied(hadItems);
        Save();
        ItemLeft?.Invoke(name, count, how);
        if (emptied) Changed?.Invoke();
    }

    // The coin tally heads the list of what the chests gave. With the last listed
    // item gone there is no list left for it to head, so it goes too and nothing
    // stays saved.
    private bool DropCoinIfEmptied(bool hadItems)
    {
        if (!hadItems || !_ledger.IsEmpty) return false;
        _coin = CurrencyHoldings.Empty;
        _log?.Info(LogCategory, "the last listed item is gone — list cleared");
        return true;
    }

    private void OnProfileLoaded(CharacterProfile _) => _post(() =>
    {
        ++_generation;
        _step = Step.Idle;
        _queued.Clear();
        _followUp.Clear();
        _beforeOverride = null;
        _afterRead = null;
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
        _inventory.Changed -= OnInventoryChanged;
        _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        _inventory.ItemSold -= OnItemSold;
        _inventory.ItemDropped -= OnItemDropped;
        _inventory.ItemHidden -= OnItemHidden;
        _inventory.ItemGivenAway -= OnItemGivenAway;
        _typedOpen.OpenSent -= OnTypedOpen;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
