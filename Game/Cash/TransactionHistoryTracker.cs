namespace MudPlay.Game.Cash;

// A per-session ledger of cash/item offloads for the Session Stats →
// Transaction history window. Records one TransactionEntry per bank `dep`osit and
// per stash-room `hide` (coin or item), each with the wall-clock time, the store
// kind, a rendered description of what was put away, and the room it happened in
// ("Name (map/room)" — the bank used or the stash room).
//
// Sourced from the server's own confirmation echoes so a MANUAL `dep` / `hide`
// typed by the user is captured the same as an automated reroute: deposits from
// InventoryManager.BankDeposited (`You deposit …`), coin stashes from
// CashManager.CoinHidden (`You hid N <coin>.`), item stashes from
// InventoryManager.ItemHidden (`You hid <item>.`).
//
// Coin stashed in a room is ONE row per room, not one per echo: a loop passing its
// stash every lap would otherwise bury the ledger in "Hid 94 silver" rows. The row
// carries the last visit's amount, the average per visit and the running total
// ("Hid 1 gold, 115 silver — avg 1 gold, 8 silver over 48 stashes — total 34 gold,
// 5,210 silver"), takes the latest time and moves to the newest end on each stash.
// Item hides stay one row each.
//
// Owns no source subscriptions — AppServices wires those echo events to the Note*
// forwarders — matching SessionActivityTracker and keeping the tracker
// dependency-free behind an injectable clock for unit tests. Every write and
// Snapshot runs on the marshalled dispatch thread (the sources all fire there),
// so the list is lock-free. Reset on the same session boundary as the other
// session-stats trackers (connect / character switch, the window "Reset session"
// button, and @reset).
public sealed class TransactionHistoryTracker
{
    // Hard cap on retained entries; the oldest is evicted past this so a long
    // session can't grow the ledger unbounded.
    public const int MaxEntries = 500;

    private readonly Func<DateTimeOffset> _clock;
    private readonly List<TransactionEntry> _entries = new();

    // Coin echoes this close together are one visit to the stash (the game answers a
    // multi-denomination stash with one line per coin type), so they share the row's
    // "last" amount; a later one starts it afresh.
    private static readonly TimeSpan VisitWindow = TimeSpan.FromSeconds(10);

    private const string PartSeparator = " — ";
    private const string AveragePrefix = "avg ";
    private const string TotalPrefix = "total ";

    // The running coin tally behind each room's row, keyed by its location label.
    private readonly Dictionary<string, StashTally> _stashByLocation = new(StringComparer.Ordinal);

    private sealed class StashTally
    {
        public DateTimeOffset LastAt;
        public int Visits;
        public readonly Dictionary<string, long> Last = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> Total = new(StringComparer.OrdinalIgnoreCase);
        public TransactionEntry Entry;
    }

    // Raised after any input records or clears an entry, so the Transaction
    // history VM can rebuild. Fires on the dispatch thread.
    public event Action? Changed;

    // Raised with each newly recorded entry (not on Reset), so a persistent log
    // can append it. Kept separate from Changed because the bounded ledger
    // evicts its oldest row past MaxEntries — diffing snapshots would miss the
    // append, so the tracker hands the fresh entry over directly.
    public event Action<TransactionEntry>? EntryAdded;

    // Raised when a room's coin row is updated in place: the row it replaces, then the
    // new one. The persistent log rewrites itself and the window carries a "keep"
    // mark across.
    public event Action<TransactionEntry, TransactionEntry>? EntryReplaced;

    public TransactionHistoryTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    // Record a bank deposit of the given copper wealth value. Non-positive
    // amounts are ignored (nothing was deposited). location is the bank room
    // ("Name (map/room)") captured at echo time, or null when unknown.
    public void NoteBankDeposit(long copper, string? location = null)
    {
        if (copper <= 0) return;
        Add(TransactionKind.Bank, $"Deposited {copper:N0} wealth", location);
    }

    // Record a stash-room hide of the given per-denomination coin amounts and
    // item names. A dispatch with neither coins nor items is ignored (the stash
    // event never fires empty, but the guard keeps the ledger clean). location
    // is the stash room ("Name (map/room)") captured at echo time, or null when
    // unknown.
    public void NoteStash(
        IReadOnlyList<(string Currency, long Amount)> currencies,
        IReadOnlyList<string> items,
        string? location = null)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(items);
        if (currencies.Count == 0 && items.Count == 0) return;
        if (items.Count > 0) Add(TransactionKind.Stash, FormatItems(items), location);
        if (currencies.Count > 0) AddCoinStash(currencies, location);
    }

    private void AddCoinStash(IReadOnlyList<(string Currency, long Amount)> currencies, string? location)
    {
        DateTimeOffset now = _clock();
        string key = location ?? string.Empty;
        bool replacing = _stashByLocation.TryGetValue(key, out StashTally? tally);
        tally ??= _stashByLocation[key] = new StashTally();

        TransactionEntry old = tally.Entry;
        if (replacing) _entries.Remove(old);
        if (!replacing || now - tally.LastAt > VisitWindow)
        {
            tally.Last.Clear();
            tally.Visits++;
        }
        foreach ((string currency, long amount) in currencies)
        {
            if (amount <= 0) continue;
            tally.Last[currency] = tally.Last.GetValueOrDefault(currency) + amount;
            tally.Total[currency] = tally.Total.GetValueOrDefault(currency) + amount;
        }
        tally.LastAt = now;
        tally.Entry = new TransactionEntry(now, TransactionKind.Stash, FormatCoinRow(tally), location);
        _entries.Add(tally.Entry);
        Evict();

        if (replacing) EntryReplaced?.Invoke(old, tally.Entry);
        else EntryAdded?.Invoke(tally.Entry);
        Changed?.Invoke();
    }

    // Point-in-time copy of the ledger, oldest entry first.
    public IReadOnlyList<TransactionEntry> Snapshot() => _entries.ToArray();

    // Replace the ledger wholesale with persisted history read back from disk on
    // reconnect. Fires Changed (so the window rebuilds) but deliberately NOT
    // EntryAdded — these rows are already on disk, and re-firing the append hook
    // would write them a second time. A full replace is safe under repeated
    // profile / BBS re-fires: each call reflects the current disk tail exactly.
    public void Hydrate(IReadOnlyList<TransactionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        _stashByLocation.Clear();
        // Fold every coin row for a room into that room's one row — a log written
        // before the roll-up holds one per echo. A rolled-up row brings its own total
        // and visit count; an older one is a single echo, and echoes a moment apart
        // are one visit.
        foreach (TransactionEntry e in entries)
        {
            if (e.Kind != TransactionKind.Stash || !TryParseCoinRow(e.Detail, out CoinRow row))
            {
                _entries.Add(e);
                continue;
            }
            string key = e.Location ?? string.Empty;
            bool known = _stashByLocation.TryGetValue(key, out StashTally? tally);
            tally ??= _stashByLocation[key] = new StashTally();
            if (known) _entries.Remove(tally.Entry);

            bool sameVisit = known && row.Visits is null && e.Time - tally.LastAt <= VisitWindow;
            if (!sameVisit)
            {
                tally.Last.Clear();
                tally.Visits += row.Visits ?? 1;
            }
            foreach ((string c, long n) in row.Last) tally.Last[c] = tally.Last.GetValueOrDefault(c) + n;
            foreach ((string c, long n) in row.Total) tally.Total[c] = tally.Total.GetValueOrDefault(c) + n;
            tally.LastAt = e.Time;
            tally.Entry = new TransactionEntry(e.Time, TransactionKind.Stash, FormatCoinRow(tally), e.Location);
            _entries.Add(tally.Entry);
        }
        Evict();
        Changed?.Invoke();
    }

    // Clear the ledger — called on the connect / character-switch boundary and by
    // the manual / remote session reset, matching the other session-stats
    // trackers.
    public void Reset()
    {
        _entries.Clear();
        _stashByLocation.Clear();
        Changed?.Invoke();
    }

    private void Add(TransactionKind kind, string detail, string? location = null)
    {
        TransactionEntry entry = new(_clock(), kind, detail, location);
        _entries.Add(entry);
        Evict();
        EntryAdded?.Invoke(entry);
        Changed?.Invoke();
    }

    // Drop the oldest rows past the cap; a room's coin row that ages out takes its
    // tally with it, so the next stash there starts a new one.
    private void Evict()
    {
        while (_entries.Count > MaxEntries)
        {
            TransactionEntry gone = _entries[0];
            _entries.RemoveAt(0);
            string key = gone.Location ?? string.Empty;
            if (_stashByLocation.TryGetValue(key, out StashTally? tally) && tally.Entry == gone)
                _stashByLocation.Remove(key);
        }
    }

    // "Hid a torch ×3" — identical item tokens fold into "name ×N" (MajorMUD lists
    // each carried copy separately).
    private static string FormatItems(IReadOnlyList<string> items)
    {
        List<string> parts = new();
        foreach (IGrouping<string, string> g in items.GroupBy(i => i, StringComparer.Ordinal))
        {
            int n = g.Count();
            parts.Add(n > 1 ? $"{g.Key} ×{n}" : g.Key);
        }
        return $"Hid {string.Join(", ", parts)}";
    }

    // "Hid 1 gold, 115 silver — avg 1 gold, 8 silver over 48 stashes — total 34 gold,
    // 5,210 silver": the last visit's amounts, the average visit, then everything stashed
    // in the room. Last and total stay in the coins that were hidden; the average is
    // worked out by value and shown in the fewest coins.
    private static string FormatCoinRow(StashTally tally)
    {
        long average = CopperValue(tally.Total) / Math.Max(1, tally.Visits);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Hid {FormatCoins(tally.Last)}{PartSeparator}{AveragePrefix}{FormatValue(average)} over {tally.Visits:N0} {(tally.Visits == 1 ? "stash" : "stashes")}"
            + $"{PartSeparator}{TotalPrefix}{FormatCoins(tally.Total)}");
    }

    // The five denominations, highest first, with what each is worth in copper.
    private static readonly (string Name, long Copper)[] Denominations =
    {
        ("runic", 1_000_000), ("platinum", 10_000), ("gold", 100), ("silver", 10), ("copper", 1),
    };

    private static int Rank(string coin) =>
        Array.FindIndex(Denominations, d => d.Name.Equals(coin, StringComparison.OrdinalIgnoreCase));

    // A word outside the standard five is a board's own name for its top coin.
    private static long CopperValue(Dictionary<string, long> coins)
    {
        long total = 0;
        foreach ((string coin, long n) in coins)
            total += n * Denominations[Math.Max(0, Rank(coin))].Copper;
        return total;
    }

    private static string FormatValue(long copper)
    {
        List<string> parts = new();
        foreach ((string name, long worth) in Denominations)
        {
            long n = copper / worth;
            if (n <= 0) continue;
            copper -= n * worth;
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n:N0} {name}"));
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "0 copper";
    }

    // Highest denomination first; a board's own top-coin word leads.
    private static string FormatCoins(Dictionary<string, long> coins) =>
        string.Join(", ", coins
            .OrderBy(kv => Rank(kv.Key))
            .Select(kv => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kv.Value:N0} {kv.Key}")));

    // One coin row read back. Visits is null for the older one-echo "Hid 115 silver",
    // whose amount is both its last and its total.
    private readonly record struct CoinRow(
        Dictionary<string, long> Last, Dictionary<string, long> Total, int? Visits);

    // False for an item row or anything that isn't purely "<count> <coin>" parts.
    private static bool TryParseCoinRow(string detail, out CoinRow row)
    {
        row = default;
        if (!detail.StartsWith("Hid ", StringComparison.Ordinal)) return false;
        string[] parts = detail[4..].Split(PartSeparator);
        Dictionary<string, long> last = new(StringComparer.OrdinalIgnoreCase);
        if (!TryParseCoins(parts[0], last)) return false;
        if (parts.Length == 1)
        {
            row = new CoinRow(last, last, null);
            return true;
        }

        Dictionary<string, long> total = new(StringComparer.OrdinalIgnoreCase);
        int visits = 0;
        foreach (string part in parts.Skip(1))
        {
            if (part.StartsWith(TotalPrefix, StringComparison.Ordinal))
            {
                if (!TryParseCoins(part[TotalPrefix.Length..], total)) return false;
            }
            else if (part.StartsWith(AveragePrefix, StringComparison.Ordinal))
            {
                // "avg 12 gold, 5 silver over 48 stashes" — only the count is read back;
                // the average is recomputed from the total.
                int over = part.LastIndexOf(" over ", StringComparison.Ordinal);
                if (over < 0) return false;
                string count = part[(over + 6)..];
                int end = count.IndexOf(' ');
                if (end > 0) count = count[..end];
                if (!int.TryParse(count, System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.InvariantCulture, out visits))
                    return false;
            }
            else return false;
        }
        if (total.Count == 0 || visits <= 0) return false;
        row = new CoinRow(last, total, visits);
        return true;
    }

    private static bool TryParseCoins(string text, Dictionary<string, long> into)
    {
        // The thousands separator is a comma too, so parts split on ", " — a comma
        // inside a number is never followed by a space.
        foreach (string part in text.Split(", "))
        {
            int space = part.IndexOf(' ');
            if (space <= 0 || part.IndexOf(' ', space + 1) >= 0) return false;
            if (!long.TryParse(part[..space], System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.InvariantCulture, out long n) || n <= 0)
                return false;
            string coin = part[(space + 1)..];
            into[coin] = into.GetValueOrDefault(coin) + n;
        }
        return into.Count > 0;
    }
}
