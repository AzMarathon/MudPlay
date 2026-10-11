using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game;

namespace MudPlay.Services;

// Live mirror of the imported MajorMUD game data on disk. Holds one GameTable per
// loaded table for the currently active set, exposes set switching with an
// ActiveSetChanged event, and provides the eviction primitive consumers use to say
// they have folded a table into typed model collections of their own.
//
// Storage layout is the one written by MdbImporter:
// Data/game data/{set-name}/{table-name}.json. Each subdirectory of
// AppPaths.GameDataRoot is a switchable set; the set picker on the Game Data menu
// reads AvailableSets.
//
// Tables are loaded lazily — SwitchSet only flips the active set name and clears
// the prior set's table cache; consumers pull tables on demand via GetTable.
// This keeps the working set small when only a few tables are actually needed
// for a given subsystem.
//
// A table is read straight from its file into a GameTable (see there): the parsed
// JSON document is never held. Monsters and Items are looked up on every room and
// combat line, so they stay loaded all session; held as documents they were 31 MB
// of a Paradigm session, and a set fresh from loading held 94 MB of them.
//
// Memory hygiene: a consumer that has built its own structures from a table calls
// EvictTable, and a table nobody has read for IdleEvictAfter is dropped by a
// background sweep, so a table read only while a set loads (Rooms is the largest
// by far) doesn't sit in memory for the whole session; the next read re-reads it.
// EvictAll wipes everything without changing the active set.
//
// Evicting only drops the cache's reference. A GameTable is immutable, and a
// reader still holding it or one of its rows (a background build mid-scan, a row
// kept in a field) reads on unharmed, which is what makes eviction safe from any
// thread.
//
// Wiring: AppServices constructs the cache and subscribes it to
// ProfileService.ProfileLoaded + ProfileService.BbsPinApplied so the pinned BBS's
// BbsProfile.ActiveGameDataSet drives the switch automatically (falling back to
// GlobalSettings.DefaultGameDataSet when no BBS is pinned). Manual switches via
// the File → Game Data → Active set menu write back to the resolved BBS profile.
public sealed class GameDataCache
{
    // FindRowByNumber / FindRowByName / RowNumbers go through each table's own
    // number and name indexes, built on its first lookup; they go when it does.
    private readonly Dictionary<string, GameTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    // Tables whose on-disk JSON failed to parse for the active set (e.g. binary
    // corruption from a pre-fix MDB import — see the multi-page LVAL memo reader
    // history). Remembered so a broken file is reported once via Log rather than
    // re-read and re-thrown on every lookup a caller makes against it; cleared
    // alongside _tables whenever the set reloads, so a re-import gets picked up.
    private readonly HashSet<string> _failedTables = new(StringComparer.OrdinalIgnoreCase);

    // Background-parsed tables for a set that ISN'T necessarily active yet — see
    // PrewarmAsync. Keyed on the exact (set, table) pair so a wrong guess just sits
    // here unclaimed instead of contaminating _tables for whatever set actually
    // ends up active. Guarded by the same _tables lock; never touched by SwitchSet
    // / EvictAll, which only ever own the live set's cache (ReloadActiveSet does
    // purge this set's stale entries after a re-import — see there, and EvictIdle
    // drops a guess nobody claimed).
    private readonly Dictionary<(string Set, string Table), ReadOutcome> _prewarmed = new();

    // Environment.TickCount64 of each cached table's last GetTable read (and of
    // each prewarm's parse), for the idle sweep. Guarded by the _tables lock.
    private readonly Dictionary<string, long> _lastRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Set, string Table), long> _prewarmedAt = new();

    // What one read of a table file came to: the table, or why there is none.
    private readonly record struct ReadOutcome(GameTable? Table, Exception? Failure, long Kilobytes);

    // A read of a table file that is running, or done and not yet put in its place.
    // A lookup that arrives meanwhile waits for it (Lazy lets one thread run the read
    // and the others block on it) rather than reading the same file a second time on
    // its own thread: a set load starts background builds that ask for the tables
    // the loading thread is reading, Rooms among them.
    private sealed class TableRead
    {
        public required Lazy<ReadOutcome> Outcome { get; init; }
        // A prewarm reads for a set that may not be active yet, so any load of that
        // set may take it (ReloadActiveSet removes the ones a re-import outdated). A
        // lookup's read belongs to the load that started it: Generation says which.
        public required bool Prewarm { get; init; }
        public long Generation { get; init; }
        // A lookup is waiting on this read and will put the table in place itself,
        // taking the entry out as it does; a prewarm then files nothing.
        public bool Claimed { get; set; }
    }

    // Guarded by the _tables lock. A table's name is matched as _tables matches it,
    // whatever its case, so "monsters" waits on a read of "Monsters".
    private readonly Dictionary<(string Set, string Table), TableRead> _reading = new(ReadKeyComparer.Instance);

    private sealed class ReadKeyComparer : IEqualityComparer<(string Set, string Table)>
    {
        public static readonly ReadKeyComparer Instance = new();

        public bool Equals((string Set, string Table) x, (string Set, string Table) y) =>
            string.Equals(x.Set, y.Set, StringComparison.Ordinal)
            && string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Set, string Table) key) =>
            HashCode.Combine(key.Set, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Table));
    }

    // Raised on the reading thread just before a table file is opened, with the
    // table's name. A test seam: counts the reads and holds one open.
    internal Action<string>? TableReadStarting { get; set; }

    // Results worked out from the active set's tables (Derived), and a count bumped
    // whenever the set's tables are dropped for re-reading, so a result built from
    // the old ones is never kept. Guarded by the _tables lock.
    private readonly Dictionary<string, object> _derived = new(StringComparer.Ordinal);
    private long _generation;

    // One load of one set: the set's name and a count that moves every time its
    // tables are dropped for re-reading (a switch, a reload, a set imported again
    // over itself). The name alone does not change on the last two.
    public readonly record struct Load(string? Set, long Generation);

    // The load the cache is serving. Whatever keeps something built from the tables
    // and decides for itself when it is out of date reads this BEFORE building and
    // keeps it with the result, which is good only while this still equals it. A
    // build a switch overtook is then never mistaken for one of the set switched
    // back to, whatever tables it read on the way. Asked on every lookup of such an
    // index, so it takes no lock: the count alone tells two loads apart, and the
    // name beside it need not be read in the same instant.
    public Load ActiveLoad => new(ActiveSet, Interlocked.Read(ref _generation));

    // Long enough that anything read per combat round / tick / room entry never ages
    // out; short enough that a startup-only table is gone within minutes of its build.
    public static readonly TimeSpan IdleEvictAfter = TimeSpan.FromMinutes(5);

    private readonly Timer? _idleSweep;

    // Root the cache walks (AppPaths.GameDataRoot). Captured at construction.
    public string GameDataRoot { get; }

    // Name of the active set, or null when nothing is selected.
    public string? ActiveSet { get; private set; }

    // Formula family the active set targets, derived from its Info table:
    // Legit == 2 → RealmType.ParaMud (GreaterMUD / Paradigm), anything else →
    // RealmType.Stock. Returns RealmType.Stock when no set is active or the Info
    // table is missing / malformed (Stock is the safe default — most realms are
    // Stock-derived).
    //
    // Reads through GetTable, so the first access lazily loads (and caches)
    // Info.json. The realm only changes when the active set changes, so callers
    // can read this per-calculation cheaply; the Info table is tiny.
    public RealmType ActiveRealm
    {
        get
        {
            GameTable? info = GetTable("Info");
            if (info is null) return RealmType.Stock;
            foreach (GameRow row in info.Rows)
            {
                if (row.TryGetProperty("Legit", out GameValue legit)
                    && legit.ValueKind == JsonValueKind.Number
                    && legit.TryGetInt32(out int code))
                {
                    return code == 2 ? RealmType.ParaMud : RealmType.Stock;
                }
            }
            return RealmType.Stock;
        }
    }

    // Whether the active set is the unmodified Stock world: its Info row says
    // Legit 1 and Custom "Default", as the 1.11p export does. ActiveRealm calls
    // everything that isn't Paradigm Stock, which is right for the formulas; a
    // board whose sysop edited the rooms is Stock that way and still isn't the
    // world the Stock files describe room by room. False with no set active.
    public bool ActiveSetIsVanillaStock
    {
        get
        {
            GameTable? info = GetTable("Info");
            if (info is null || info.Rows.Count == 0)
                return false;
            // Info.json is a one-element array holding the export's own description.
            GameRow row = info.Rows[0];
            return row.TryGetProperty("Legit", out GameValue legit)
                && legit.ValueKind == JsonValueKind.Number && legit.TryGetInt32(out int code) && code == 1
                && row.TryGetProperty("Custom", out GameValue custom)
                && custom.ValueKind == JsonValueKind.String
                && string.Equals(custom.GetString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Fires whenever ActiveSet changes — including the transition from a real set
    // to null (no set picked). Carries the new set name. Subscribers should drop
    // any per-set state they had cached and re-pull whatever they need from the
    // cache.
    public event Action<string?>? ActiveSetChanged;

    // Fires once (deduped via _failedTables) the first time a table's on-disk JSON
    // fails to parse for the active set — carries the table name. A silently
    // missing table leaves engines short of data with no obvious symptom, so
    // production routes this to a red terminal notice (MainWindowViewModel) telling
    // the user to re-import the set. Cleared with the failure on evict/reload.
    public event Action<string>? TableParseFailed;

    // Optional log sink — when set (production wires AppServices.Log after
    // construction), every SwitchSet emits an Info entry naming the outgoing +
    // incoming set so the user can verify swap success in the program log. Tests
    // leave it null.
    public LogService? Log { get; set; }
    // Times table parses for the performance log; set once by AppServices.
    public PerformanceMonitor? Performance { get; set; }

    public GameDataCache() : this(AppPaths.GameDataRoot)
    {
        _idleSweep = new Timer(_ => EvictIdle(IdleEvictAfter), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // Test seam — lets tests point at an isolated root. No idle sweep; tests call
    // EvictIdle directly.
    internal GameDataCache(string gameDataRoot)
    {
        GameDataRoot = gameDataRoot;
        Directory.CreateDirectory(GameDataRoot);
    }

    // Imported sets visible on disk, alphabetical. Each entry is a folder name
    // directly under GameDataRoot — pass these to SwitchSet to activate.
    public IReadOnlyList<string> AvailableSets => EnumerateSets();

    private IReadOnlyList<string> EnumerateSets()
    {
        if (!Directory.Exists(GameDataRoot)) return Array.Empty<string>();
        return Directory.GetDirectories(GameDataRoot)
            .Select(Path.GetFileName)
            .Where(static n => !string.IsNullOrEmpty(n))
            .Select(static n => n!)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Table names currently held for ActiveSet. Empty before any GetTable call;
    // empty again after EvictAll.
    public IReadOnlyList<string> LoadedTables
    {
        get
        {
            lock (_tables) return _tables.Keys.ToArray();
        }
    }

    // Flip the active set. Pass null to clear. Fires ActiveSetChanged with the
    // new value when the effective set changes; suppresses the event on no-op
    // switches.
    //
    // Setting an unknown set name still flips ActiveSet to that string — the
    // cache assumes the caller knows what they're doing (e.g. a profile points at
    // a set that hasn't been imported yet, so the user can see the mismatch in
    // the UI rather than silently falling back).
    public void SwitchSet(string? setName)
    {
        // Normalize empty → null so the event payload is consistent.
        if (string.IsNullOrWhiteSpace(setName)) setName = null;

        if (string.Equals(ActiveSet, setName, StringComparison.OrdinalIgnoreCase)) return;

        string? outgoing = ActiveSet;
        EvictAll();
        ActiveSet = setName;
        // Again once the new set is the active one: a result built in between read
        // tables of the old.
        lock (_tables)
        {
            _derived.Clear();
            Interlocked.Increment(ref _generation);
        }
        Log?.Log(LogSeverity.Info, "GameData",
            (outgoing, setName) switch
            {
                (null, null)       => "No active game data set.",
                (null, not null)   => $"Loaded game data set '{setName}'.",
                (not null, null)   => $"Unloaded game data set '{outgoing}' (no set active).",
                (not null, not null) => $"Swapped game data set '{outgoing}' → '{setName}'.",
            });
        ActiveSetChanged?.Invoke(setName);
        LogTablesHeld();
    }

    // What the set's subscribers left loaded once they had all rebuilt, table by
    // table: the memory the set's tables hold from here until the idle sweep drops
    // the ones only the rebuild read.
    private void LogTablesHeld()
    {
        if (Log is null || ActiveSet is null) return;
        (string Table, long Bytes)[] held;
        lock (_tables)
            held = _tables.Select(t => (t.Key, t.Value.ResidentBytes)).OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();
        if (held.Length == 0) return;
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Log.Log(LogSeverity.Info, "GameData",
            $"Tables held for '{ActiveSet}': {string.Join(", ", held.Select(t => $"{t.Table} {Mb(t.Bytes)} MB"))} "
            + $"({Mb(held.Sum(t => t.Bytes))} MB in all).");
    }

    // Reload the raw cache for the current set. Cheaper than SwitchSet(null) →
    // SwitchSet(name) because no event fires.
    public void Reload()
    {
        EvictAll();
    }

    // Re-ingest the active set's tables from disk AND notify consumers. This is
    // the "re-imported over the currently-active set" case: the set name is
    // unchanged, so SwitchSet no-ops and the stale in-memory tables would linger
    // until the user swapped sets away and back. Evicting the raw cache makes the
    // next read re-parse the fresh JSON, and re-firing ActiveSetChanged (with the
    // same name) drives every store / view-model to rebuild its derived state —
    // exactly what a real swap does. No-op when no set is active.
    public void ReloadActiveSet()
    {
        if (ActiveSet is null) return;
        EvictAll();
        // Drop any still-unclaimed background prewarm for this set, running or done —
        // it predates the re-import, so claiming it later would hand back stale
        // tables. (Startup prewarm entries are normally claimed long before a
        // re-import; this covers a table that happened never to be read.) A lookup's
        // own read still running goes with them: it belongs to the load just ended.
        lock (_tables)
        {
            foreach ((string Set, string Table) key in _reading.Keys
                         .Where(k => string.Equals(k.Set, ActiveSet, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _reading.Remove(key);
            foreach ((string Set, string Table) key in _prewarmed.Keys
                         .Where(k => string.Equals(k.Set, ActiveSet, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _prewarmed.Remove(key);
                _prewarmedAt.Remove(key);
            }
        }
        Log?.Log(LogSeverity.Info, "GameData", $"Re-ingested game data set '{ActiveSet}'.");
        ActiveSetChanged?.Invoke(ActiveSet);
        LogTablesHeld();
    }

    // Lazy-load (or return the cached) table tableName of the active set. Returns
    // null when no set is active or the table file does not exist on disk. The
    // table name is matched case-insensitively against the JSON filename.
    public GameTable? GetTable(string tableName)
    {
        if (ActiveSet is null) return null;
        ArgumentNullException.ThrowIfNull(tableName);

        bool notifyParseFailed = false;
        GameTable? result = null;
        string set;
        string? path;
        long generation;
        TableRead read;
        bool mine;
        lock (_tables)
        {
            if (_tables.TryGetValue(tableName, out GameTable? cached))
            {
                _lastRead[tableName] = Environment.TickCount64;
                return cached;
            }
            if (_failedTables.Contains(tableName)) return null;

            // A PrewarmAsync call already parsed this table for the set that's now
            // active, ahead of the switch — claim it instead of re-reading the file.
            if (_prewarmed.Remove((ActiveSet, tableName), out ReadOutcome warmed))
            {
                _prewarmedAt.Remove((ActiveSet, tableName));
                _tables[tableName] = warmed.Table!;
                _lastRead[tableName] = Environment.TickCount64;
                Log?.Log(LogSeverity.Debug, "GameData",
                    $"'{tableName}' claimed from background prewarm for '{ActiveSet}'.");
                return warmed.Table;
            }

            set = ActiveSet;
            path = ResolveTablePath(set, tableName);
            if (path is null || !File.Exists(path)) return null;
            generation = _generation;

            // A read of this table is already running, for a prewarm or for another
            // lookup of this load: wait for that one. Otherwise this lookup's read
            // is the one the next lookup waits for.
            if (_reading.TryGetValue((set, tableName), out TableRead? running)
                && (running.Prewarm || running.Generation == generation))
            {
                read = running;
                mine = false;
            }
            else
            {
                string file = path;
                read = new TableRead
                {
                    Outcome = new Lazy<ReadOutcome>(() => ReadTable(file, tableName)),
                    Prewarm = false,
                    Generation = generation,
                };
                _reading[(set, tableName)] = read;
                mine = true;
            }
            read.Claimed = true;
        }

        // Read and parse outside the lock: Rooms takes half a second, and holding the
        // lock that long stalled every other table lookup, the UI thread's included,
        // behind a background read. Waiting on the Lazy holds no lock of ours, and
        // the read it runs takes none and needs no other thread, so a UI thread
        // waiting on a worker's read waits for the file and nothing else.
        ReadOutcome outcome;
        using (Performance?.Measure(mine ? $"parse {tableName}"
                   : read.Prewarm ? $"await prewarm {tableName}" : $"await parse {tableName}"))
            outcome = read.Outcome.Value;
        bool shared = !mine && outcome.Table is not null;
        // A read another caller started that failed is that caller's to answer for.
        // This one reads the file itself, as it would have with no read in flight,
        // so each caller meets the failure (or a file that reads this time) first-hand.
        if (!mine && outcome.Table is null)
        {
            using PerformanceMonitor.Scope? parse = Performance?.Measure($"parse {tableName}");
            outcome = ReadTable(path, tableName);
        }

        lock (_tables)
        {
            // Taken out here, under the same lock that puts the table in place, so a
            // lookup arriving in between finds one or the other and never reads again.
            if (_reading.TryGetValue((set, tableName), out TableRead? listed) && ReferenceEquals(listed, read))
                _reading.Remove((set, tableName));

            // The set was switched or reloaded meanwhile: hand the caller what it
            // asked for, as if the read had finished first, but don't keep it.
            if (generation != _generation)
                result = outcome.Table;
            else if (_tables.TryGetValue(tableName, out GameTable? won))
            {
                _lastRead[tableName] = Environment.TickCount64;
                return won;
            }
            else if (outcome.Table is not null)
            {
                result = outcome.Table;
                _tables[tableName] = result;
                _lastRead[tableName] = Environment.TickCount64;
                Log?.Log(LogSeverity.Debug, "GameData", shared && read.Prewarm
                    ? $"'{tableName}' taken from the prewarm in flight for '{set}'."
                    : $"'{tableName}' loaded for '{set}' ({outcome.Kilobytes} KB on disk, {result.ResidentBytes / 1024} KB held).");
            }
            else if (outcome.Failure is JsonException ex && _failedTables.Add(tableName))
            {
                // Malformed table JSON is an external-data-boundary failure (a bad
                // MDB import, hand-edited file, etc.), not an app invariant — treat
                // the table as unavailable rather than crash every consumer that
                // happens to look it up. Said once: a lookup that was reading the
                // same file meanwhile finds the table already marked.
                Log?.Log(LogSeverity.Error, "GameData",
                    $"'{tableName}' could not be parsed for set '{set}' ({ex.Message}) — " +
                    "treating it as unavailable until the set is reloaded or re-imported.");
                notifyParseFailed = true;
            }
        }

        // Fire the failure notice OUTSIDE the lock so a subscriber (production
        // routes it to a Dispatcher.Post terminal notice) never runs while we hold
        // _tables. Deduped by _failedTables: the next lookup short-circuits above,
        // so this fires at most once per table per active set.
        if (notifyParseFailed) TableParseFailed?.Invoke(tableName);
        // Anything but malformed JSON (the file locked, gone, unreadable) is the
        // caller's to hear of, as it was when the read ran on the caller's own stack.
        if (outcome.Table is null && outcome.Failure is { } fault and not JsonException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
        return result;
    }

    // One table file into its compact form, a block at a time: the file is never
    // whole in memory. The stream does no buffering of its own; the reader's block
    // is the buffer. Never throws: several lookups may be waiting on one read, and
    // each decides for itself what a failure means to it.
    private ReadOutcome ReadTable(string path, string tableName)
    {
        try
        {
            TableReadStarting?.Invoke(tableName);
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0, FileOptions.SequentialScan);
            return new ReadOutcome(GameTableReader.Read(file), null, file.Length / 1024);
        }
        catch (Exception ex)
        {
            return new ReadOutcome(null, ex, 0);
        }
    }

    // Parse tableNames for setName on background threads, ahead of setName actually
    // becoming ActiveSet. Startup's biggest latency sink is the synchronous Rooms.json
    // parse + graph rebuild GameData.SwitchSet triggers the moment the auto-loaded
    // profile resolves its BBS's game-data set — kicking the file read off early
    // lets it run concurrently with the rest of AppServices construction and the BBS
    // connect handshake that follows, instead of serially in front of both.
    //
    // Speculative and best-effort: if setName never becomes active (auto-load off, or
    // the guess was wrong), the read tables sit in _prewarmed unclaimed until the
    // idle sweep drops them. GetTable claims a match by exact (setName, tableName); a
    // read/parse failure here is silently dropped — the real GetTable call a moment
    // later hits the same failure through its normal (unprewarmed) path and reports it
    // there instead of duplicating that handling on a background thread nobody's
    // watching.
    //
    // Returns the aggregate Task so tests can await completion deterministically;
    // production startup fires this and discards it (`_ = ...`) — nothing blocks on it.
    public Task PrewarmAsync(string setName, IReadOnlyList<string> tableNames)
    {
        ArgumentNullException.ThrowIfNull(setName);
        ArgumentNullException.ThrowIfNull(tableNames);

        return Task.WhenAll(tableNames.Select(tableName =>
            {
                string? path = ResolveTablePath(setName, tableName);
                if (path is null || !File.Exists(path)) return Task.CompletedTask;

                TableRead read = new()
                {
                    Outcome = new Lazy<ReadOutcome>(() => ReadTable(path, tableName)),
                    Prewarm = true,
                };
                lock (_tables)
                {
                    if (_tables.ContainsKey(tableName) || _prewarmed.ContainsKey((setName, tableName))
                        || !_reading.TryAdd((setName, tableName), read))
                        return Task.CompletedTask;
                }
                return Task.Run(() =>
                {
                    ReadOutcome outcome = read.Outcome.Value;
                    lock (_tables)
                    {
                        // Still ours to file: a lookup that claimed it meanwhile
                        // publishes it itself, and takes the entry out when it does.
                        if (!_reading.TryGetValue((setName, tableName), out TableRead? pending)
                            || !ReferenceEquals(pending, read) || read.Claimed)
                            return;
                        _reading.Remove((setName, tableName));
                        if (outcome.Table is null) return;
                        _prewarmed[(setName, tableName)] = outcome;
                        _prewarmedAt[(setName, tableName)] = Environment.TickCount64;
                    }
                });
            }));
    }

    // True when tableName is held parsed right now; never reads it.
    public bool IsTableLoaded(string tableName)
    {
        lock (_tables) return _tables.ContainsKey(tableName);
    }

    // A result worked out from the active set's tables, built once and kept until
    // the set is switched, reloaded or re-imported: the trainer and bank lists, a
    // room-name index. Those were rebuilt by every window open and right-click that
    // listed them, each walking the raw tables (and the 22 MB Rooms table re-parsed
    // after the room graph had freed it). Evicting a table to save memory leaves
    // them, since the data they came from hasn't changed. build runs outside the
    // lock and may run twice when two threads ask at once; the first result is kept.
    public T Derived<T>(string key, Func<T> build) where T : class
    {
        ArgumentNullException.ThrowIfNull(build);
        long generation;
        lock (_tables)
        {
            if (_derived.TryGetValue(key, out object? hit)) return (T)hit;
            generation = _generation;
        }
        T value = build();
        lock (_tables)
        {
            if (generation != _generation) return value;
            if (_derived.TryGetValue(key, out object? won)) return (T)won;
            _derived[key] = value;
            return value;
        }
    }

    // Lookup the Name field of the row in tableName whose Number field equals
    // number. Returns null when the table isn't in the active set, the row isn't
    // found, or either field is missing on the matched row. Used by edit dialogs
    // to render GameDataLink back-references as human-readable labels.
    public string? FindNameByNumber(string tableName, int number)
    {
        GameRow? row = FindRowByNumber(tableName, number);
        if (row is null) return null;
        return row.Value.TryGetProperty("Name", out GameValue nameEl) ? nameEl.GetString() : null;
    }

    // The set of Number values present in tableName for the active set (empty when the
    // table isn't loadable). Lets a caller test row existence in O(1) without an O(rows)
    // scan per lookup — e.g. the Messages tab hiding records claimed by a real spell.
    public HashSet<int> RowNumbers(string tableName)
    {
        GameTable? table = GetTable(tableName);
        return table is null ? new HashSet<int>() : new HashSet<int>(table.Numbers);
    }

    // Return the full row in tableName whose Number field equals number, or
    // null when the table isn't in the active set or no row matches. Mirrors
    // FindNameByNumber but hands back the whole row so a caller can read
    // arbitrary fields (Abil-N / NegateSpell-N / Action …), not just Name. Ties go
    // to the first row in the file. The returned row stays valid for as long as
    // the caller holds it.
    public GameRow? FindRowByNumber(string tableName, int number) =>
        GetTable(tableName)?.FindByNumber(number);

    // Return the row in tableName whose Name field equals name (case-insensitive),
    // or null when the table isn't in the active set or no row matches. Ties go to
    // the first row in the file. The returned row stays valid for as long as the
    // caller holds it. Used by the trap-delegation capability lookup to resolve a
    // party member's class / race row before scanning its abilities.
    public GameRow? FindRowByName(string tableName, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        GameTable? table = GetTable(tableName);
        if (table is null) return null;
        // Normalise the lookup key: trim and collapse internal whitespace runs to a
        // single space. Game-data names are canonical (no stray spacing), but a name
        // passed in from an inventory parse can carry a doubled or trailing space —
        // the game's 'i' dump is word-wrapped, so a name split across a wrap can come
        // back as "silvery skullcap " / "silvery  skullcap". Without this, the exact
        // match silently misses and the caller reads item 0 (e.g. @uses reporting a
        // charged item as "isn't a limited-use item").
        return table.FindByName(NormalizeLookupName(name));
    }

    private static string NormalizeLookupName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");

    // A consumer is done with tableName: it has folded the rows into an index of its
    // own. The table is left for the idle sweep to drop at its next pass (within a
    // minute) unless something reads it before then. Dropping it outright made the
    // tables other code reads on every line (Monsters, Items, Spells) parse again
    // straight after each index build — Monsters nine times in a session's first
    // minute. Returns whether the table was loaded.
    public bool EvictTable(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        lock (_tables)
        {
            if (!_tables.ContainsKey(tableName)) return false;
            _lastRead[tableName] = long.MinValue / 2;   // older than any idle window
            return true;
        }
    }

    // Drop one cached table now.
    private bool DropTable(string tableName)
    {
        lock (_tables)
        {
            _failedTables.Remove(tableName);
            _lastRead.Remove(tableName);
            return _tables.Remove(tableName);
        }
    }

    // Drop every cached table (and unclaimed prewarm) not read within idle. Runs off
    // the production idle-sweep timer; safe from any thread because a dropped table
    // stays readable to whoever still holds it (see the class comment).
    internal void EvictIdle(TimeSpan idle)
    {
        long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        List<string> dropped = new();
        lock (_tables)
        {
            foreach ((string table, long at) in _lastRead.ToList())
            {
                if (at > cutoff) continue;
                if (DropTable(table)) dropped.Add(table);
            }
            foreach (((string Set, string Table) key, long at) in _prewarmedAt.ToList())
            {
                if (at > cutoff) continue;
                _prewarmed.Remove(key);
                _prewarmedAt.Remove(key);
                dropped.Add($"{key.Table} (unclaimed prewarm, '{key.Set}')");
            }
        }
        if (dropped.Count > 0)
            Log?.Log(LogSeverity.Debug, "GameData",
                $"Dropped {string.Join(", ", dropped)} — unread for {idle.TotalMinutes:0.#} min.");
    }

    // Drop every cached table. Called implicitly by SwitchSet and Reload; callers
    // can use it to free memory after a bulk-conversion pass too. Also clears
    // remembered parse failures so a re-import that fixed a broken table gets
    // re-read instead of staying marked unavailable.
    public void EvictAll()
    {
        lock (_tables)
        {
            _derived.Clear();
            Interlocked.Increment(ref _generation);
            _tables.Clear();
            _failedTables.Clear();
            _lastRead.Clear();
        }
    }

    // Resolve a table-name → JSON file path under the given set. Looks for a
    // case-insensitive match against existing files so the caller can pass either
    // the table's wire name or the on-disk filename. Returns null when no match
    // exists.
    private string? ResolveTablePath(string setName, string tableName)
    {
        string setDir = Path.Combine(GameDataRoot, setName);
        if (!Directory.Exists(setDir)) return null;

        string exact = Path.Combine(setDir, tableName + ".json");
        if (File.Exists(exact)) return exact;

        // Case-insensitive fallback — Linux is case-sensitive at the
        // FS level, so a table named "Monsters" on a Wine-imported set
        // is "Monsters.json" but a hand-renamed one could be "monsters.json".
        string target = tableName + ".json";
        return Directory.EnumerateFiles(setDir, "*.json")
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), target, StringComparison.OrdinalIgnoreCase));
    }
}
