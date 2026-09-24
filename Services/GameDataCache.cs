using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game;

namespace MudPlay.Services;

// Live mirror of the imported MajorMUD game data on disk. Holds the raw
// JsonDocument per loaded table for the currently active set, exposes set
// switching with an ActiveSetChanged event, and provides the eviction primitive
// consumers need to drop raw JSON once it has been folded into typed model
// collections (the per-tab services own those conversions).
//
// Storage layout is the one written by MdbImporter:
// Data/game data/{set-name}/{table-name}.json. Each subdirectory of
// AppPaths.GameDataRoot is a switchable set; the set picker on the Game Data menu
// reads AvailableSets.
//
// Tables are loaded lazily — SwitchSet only flips the active set name and clears
// the prior set's table cache; consumers pull tables on demand via GetRawTable.
// This keeps the working set small when only a few tables are actually needed
// for a given subsystem.
//
// Memory hygiene: once a per-tab consumer has converted the raw JsonDocument into
// typed model collections it calls EvictTable to drop the raw bytes — savings on
// large data sets land in the 150–170 MB range. EvictAll wipes everything
// without changing the active set. A table nobody has read for IdleEvictAfter is
// dropped by a background sweep too, so a table some consumer loads and never
// evicts (Rooms is ~56 MB parsed) doesn't sit in memory for the whole session;
// the next read simply re-parses it.
//
// Evicting only DROPS the reference, it never disposes. JsonDocument.Parse rents
// its metadata buffer from ArrayPool.Shared, and Dispose hands it back — the pool
// then keeps those multi-MB buffers parked for the life of the process. Dropped
// instead, the document is ordinary garbage, and a reader still holding one of its
// JsonElements (a background build mid-scan, a cached row) stays valid rather than
// hitting a disposed buffer. That's what makes eviction safe from any thread.
//
// Wiring: AppServices constructs the cache and subscribes it to
// ProfileService.ProfileLoaded + ProfileService.BbsPinApplied so the pinned BBS's
// BbsProfile.ActiveGameDataSet drives the switch automatically (falling back to
// GlobalSettings.DefaultGameDataSet when no BBS is pinned). Manual switches via
// the File → Game Data → Active set menu write back to the resolved BBS profile.
public sealed class GameDataCache
{
    private readonly Dictionary<string, JsonDocument> _tables = new(StringComparer.OrdinalIgnoreCase);

    // Lazy per-table indexes backing FindRowByNumber / FindRowByName / RowNumbers —
    // built once on first lookup against a table instead of re-walking
    // EnumerateArray() on every call. A hot per-tick consumer (KnownSpellCatalog's
    // buff/cast-item resolution) was re-scanning the Spells/Items tables from
    // scratch on every TickEngine tick, stalling the UI thread; see GetNumberIndex.
    // Cleared alongside _tables in EvictTable / EvictAll so a reload rebuilds them.
    private readonly Dictionary<string, Dictionary<int, JsonElement>> _numberIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _nameIndex = new(StringComparer.OrdinalIgnoreCase);

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
    private readonly Dictionary<(string Set, string Table), JsonDocument> _prewarmed = new();

    // Environment.TickCount64 of each cached table's last GetRawTable read (and of
    // each prewarm's parse), for the idle sweep. Guarded by the _tables lock.
    private readonly Dictionary<string, long> _lastRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Set, string Table), long> _prewarmedAt = new();

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
    // Reads through GetRawTable, so the first access lazily loads (and caches)
    // Info.json. The realm only changes when the active set changes, so callers
    // can read this per-calculation cheaply; the Info table is tiny.
    public RealmType ActiveRealm
    {
        get
        {
            JsonDocument? doc = GetRawTable("Info");
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return RealmType.Stock;
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (row.TryGetProperty("Legit", out JsonElement legit)
                    && legit.ValueKind == JsonValueKind.Number
                    && legit.TryGetInt32(out int code))
                {
                    return code == 2 ? RealmType.ParaMud : RealmType.Stock;
                }
            }
            return RealmType.Stock;
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

    // Table names currently held in the raw cache for ActiveSet. Empty before any
    // GetRawTable call; empty again after EvictAll.
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
        Log?.Log(LogSeverity.Info, "GameData",
            (outgoing, setName) switch
            {
                (null, null)       => "No active game data set.",
                (null, not null)   => $"Loaded game data set '{setName}'.",
                (not null, null)   => $"Unloaded game data set '{outgoing}' (no set active).",
                (not null, not null) => $"Swapped game data set '{outgoing}' → '{setName}'.",
            });
        ActiveSetChanged?.Invoke(setName);
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
        // Drop any still-unclaimed background prewarm for this set — it predates the
        // re-import, so claiming it later would hand back stale tables. (Startup
        // prewarm entries are normally claimed long before a re-import; this covers
        // a table that happened never to be read.)
        lock (_tables)
        {
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
    }

    // Lazy-load (or return the cached) JsonDocument for tableName in the active
    // set. Returns null when no set is active or the table file does not exist on
    // disk. The table name is matched case-insensitively against the JSON
    // filename.
    public JsonDocument? GetRawTable(string tableName)
    {
        if (ActiveSet is null) return null;
        ArgumentNullException.ThrowIfNull(tableName);

        bool notifyParseFailed = false;
        JsonDocument? result = null;
        lock (_tables)
        {
            if (_tables.TryGetValue(tableName, out JsonDocument? cached))
            {
                _lastRead[tableName] = Environment.TickCount64;
                return cached;
            }
            if (_failedTables.Contains(tableName)) return null;

            // A PrewarmAsync call already parsed this table for the set that's now
            // active, ahead of the switch — claim it instead of re-reading the file.
            if (_prewarmed.Remove((ActiveSet, tableName), out JsonDocument? warmed))
            {
                _prewarmedAt.Remove((ActiveSet, tableName));
                _tables[tableName] = warmed;
                _lastRead[tableName] = Environment.TickCount64;
                Log?.Log(LogSeverity.Debug, "GameData",
                    $"'{tableName}' claimed from background prewarm for '{ActiveSet}'.");
                return warmed;
            }

            string? path = ResolveTablePath(ActiveSet, tableName);
            if (path is null || !File.Exists(path)) return null;

            // ReadAllBytes is fine — these JSON files are tens of MB at
            // most and we don't want to hold a FileStream while Parse
            // walks the buffer.
            byte[] bytes = File.ReadAllBytes(path);

            try
            {
                result = JsonDocument.Parse(bytes);
                _tables[tableName] = result;
                _lastRead[tableName] = Environment.TickCount64;
                Log?.Log(LogSeverity.Debug, "GameData",
                    $"'{tableName}' loaded for '{ActiveSet}' ({bytes.Length / 1024} KB).");
            }
            catch (JsonException ex)
            {
                // Malformed table JSON is an external-data-boundary failure (a bad
                // MDB import, hand-edited file, etc.), not an app invariant — treat
                // the table as unavailable rather than crash every consumer that
                // happens to look it up.
                _failedTables.Add(tableName);
                Log?.Log(LogSeverity.Error, "GameData",
                    $"'{tableName}' could not be parsed for set '{ActiveSet}' ({ex.Message}) — " +
                    "treating it as unavailable until the set is reloaded or re-imported.");
                notifyParseFailed = true;
            }
        }

        // Fire the failure notice OUTSIDE the lock so a subscriber (production
        // routes it to a Dispatcher.Post terminal notice) never runs while we hold
        // _tables. Deduped by _failedTables: the next lookup short-circuits above,
        // so this fires at most once per table per active set.
        if (notifyParseFailed) TableParseFailed?.Invoke(tableName);
        return result;
    }

    // Parse tableNames for setName on background threads, ahead of setName actually
    // becoming ActiveSet. Startup's biggest latency sink is the synchronous Rooms.json
    // parse + graph rebuild GameData.SwitchSet triggers the moment the auto-loaded
    // profile resolves its BBS's game-data set — kicking the raw-JSON parse off early
    // lets it run concurrently with the rest of AppServices construction and the BBS
    // connect handshake that follows, instead of serially in front of both.
    //
    // Speculative and best-effort: if setName never becomes active (auto-load off, or
    // the guess was wrong), the parsed documents sit in _prewarmed unclaimed until the
    // idle sweep drops them. GetRawTable claims a match by exact (setName, tableName); a
    // read/parse failure here is silently dropped — the real GetRawTable call a moment
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

        return Task.WhenAll(tableNames.Select(tableName => Task.Run(() =>
            {
                string? path = ResolveTablePath(setName, tableName);
                if (path is null || !File.Exists(path)) return;

                JsonDocument doc;
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    doc = JsonDocument.Parse(bytes);
                }
                catch
                {
                    return;
                }

                lock (_tables)
                {
                    if (_tables.ContainsKey(tableName) || _prewarmed.ContainsKey((setName, tableName)))
                        return;
                    _prewarmed[(setName, tableName)] = doc;
                    _prewarmedAt[(setName, tableName)] = Environment.TickCount64;
                }
            })));
    }

    // Try-get variant of GetRawTable.
    public bool TryGetRawTable(string tableName, out JsonDocument? doc)
    {
        doc = GetRawTable(tableName);
        return doc is not null;
    }

    // Lookup the Name field of the row in tableName whose Number field equals
    // number. Returns null when the table isn't in the active set, the row isn't
    // found, or either field is missing on the matched row. Used by edit dialogs
    // to render GameDataLink back-references as human-readable labels.
    public string? FindNameByNumber(string tableName, int number)
    {
        JsonElement? row = FindRowByNumber(tableName, number);
        if (row is null) return null;
        return row.Value.TryGetProperty("Name", out JsonElement nameEl) ? nameEl.GetString() : null;
    }

    // The set of Number values present in tableName for the active set (empty when the
    // table isn't loadable). Lets a caller test row existence in O(1) without an O(rows)
    // scan per lookup — e.g. the Messages tab hiding records claimed by a real spell.
    public HashSet<int> RowNumbers(string tableName)
    {
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return new HashSet<int>();
        return new HashSet<int>(GetNumberIndex(tableName, doc).Keys);
    }

    // Return the full row in tableName whose Number field equals number, or
    // null when the table isn't in the active set or no row matches. Mirrors
    // FindNameByNumber but hands back the whole JsonElement so a caller can
    // read arbitrary fields (Abil-N / NegateSpell-N / Action …), not just Name.
    // The returned JsonElement stays valid for as long as the caller holds it.
    // Backs the room-hazard index's Spells / Items / TBInfo record reads.
    public JsonElement? FindRowByNumber(string tableName, int number)
    {
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return null;
        return GetNumberIndex(tableName, doc).TryGetValue(number, out JsonElement row) ? row : null;
    }

    // Return the row in tableName whose Name field equals name (case-insensitive),
    // or null when the table isn't in the active set or no row matches. The
    // returned JsonElement stays valid for the lifetime of the cached
    // JsonDocument (until the next SwitchSet / EvictTable). Used by the
    // trap-delegation capability lookup to resolve a party member's class / race
    // row before scanning its abilities.
    public JsonElement? FindRowByName(string tableName, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        JsonDocument? doc = GetRawTable(tableName);
        if (doc is null) return null;
        // Normalise the lookup key: trim and collapse internal whitespace runs to a
        // single space. Game-data names are canonical (no stray spacing), but a name
        // passed in from an inventory parse can carry a doubled or trailing space —
        // the game's 'i' dump is word-wrapped, so a name split across a wrap can come
        // back as "silvery skullcap " / "silvery  skullcap". Without this, the exact
        // match silently misses and the caller reads item 0 (e.g. @uses reporting a
        // charged item as "isn't a limited-use item").
        string key = NormalizeLookupName(name);
        return GetNameIndex(tableName, doc).TryGetValue(key, out JsonElement row) ? row : null;
    }

    private static string NormalizeLookupName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");

    // Build (or return the cached) Number → row index for tableName. Ties are
    // resolved first-match-wins, matching the linear scan this replaced. Built
    // once per table load; invalidated alongside _tables.
    private Dictionary<int, JsonElement> GetNumberIndex(string tableName, JsonDocument doc)
    {
        lock (_tables)
        {
            if (_numberIndex.TryGetValue(tableName, out Dictionary<int, JsonElement>? index)) return index;

            index = new Dictionary<int, JsonElement>();
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (numEl.TryGetInt32(out int n)) index.TryAdd(n, row);
            }
            if (IsCurrent(tableName, doc)) _numberIndex[tableName] = index;
            return index;
        }
    }

    // Build (or return the cached) Name → row index for tableName, case-insensitive.
    // Ties are resolved first-match-wins, matching the linear scan this replaced.
    // Built once per table load; invalidated alongside _tables.
    private Dictionary<string, JsonElement> GetNameIndex(string tableName, JsonDocument doc)
    {
        lock (_tables)
        {
            if (_nameIndex.TryGetValue(tableName, out Dictionary<string, JsonElement>? index)) return index;

            index = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
                if (nameEl.ValueKind != JsonValueKind.String) continue;
                string? name = nameEl.GetString();
                if (name is not null) index.TryAdd(name, row);
            }
            if (IsCurrent(tableName, doc)) _nameIndex[tableName] = index;
            return index;
        }
    }

    // An eviction (the idle sweep runs on a timer thread) can land between a caller's
    // GetRawTable and its index build. Caching that index would pin the evicted
    // document behind a table the cache no longer holds, so it's only served once.
    private bool IsCurrent(string tableName, JsonDocument doc)
        => _tables.TryGetValue(tableName, out JsonDocument? cached) && ReferenceEquals(cached, doc);

    // Drop the cached JsonDocument for one table. Used by per-tab consumers after
    // they've folded the raw JSON into typed model collections.
    public bool EvictTable(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        lock (_tables)
        {
            _failedTables.Remove(tableName);
            _numberIndex.Remove(tableName);
            _nameIndex.Remove(tableName);
            _lastRead.Remove(tableName);
            return _tables.Remove(tableName);
        }
    }

    // Drop every cached table (and unclaimed prewarm) not read within idle. Runs off
    // the production idle-sweep timer; safe from any thread because eviction never
    // disposes (see the class comment).
    internal void EvictIdle(TimeSpan idle)
    {
        long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        List<string> dropped = new();
        lock (_tables)
        {
            foreach ((string table, long at) in _lastRead.ToList())
            {
                if (at > cutoff) continue;
                if (EvictTable(table)) dropped.Add(table);
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
            _tables.Clear();
            _failedTables.Clear();
            _numberIndex.Clear();
            _nameIndex.Clear();
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
