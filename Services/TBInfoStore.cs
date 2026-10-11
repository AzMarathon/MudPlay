using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// In-memory index of TBInfo.json for the active game-data set. Mirrors
// RoomGraphManager's lifecycle: subscribes to GameDataCache.ActiveSetChanged,
// loads the table via GameDataCache.GetTable, evicts it after typed
// conversion (memory-hygiene parity), and re-publishes StoreReloaded for
// consumers. The teleport handler parses each entry's Action directive chain
// into the keyword → destination map the walker uses.
public sealed class TBInfoStore
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    // Replaced whole on every reload, never refilled: ItemSourceIndex and
    // RoomFloorItemIndex walk the entries on a worker thread while the UI thread may
    // be loading the next set, and a walk must see one set's entries from end to end.
    private volatile Dictionary<int, TBInfoEntry> _entries = new();

    // Active set the store was last loaded from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of entries in the active store (0 when no set is active or load
    // failed).
    public int EntryCount => _entries.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active (empty store). Subscribers should drop any cached TBInfo
    // references and re-pull what they need.
    public event Action? StoreReloaded;

    public TBInfoStore(GameDataCache cache) : this(cache, log: null) { }

    public TBInfoStore(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Look up a single entry by its primary key. Returns null when absent.
    public TBInfoEntry? GetEntry(int number)
        => _entries.TryGetValue(number, out TBInfoEntry? e) ? e : null;

    // Read-only snapshot of every entry in load order. Empty when no set is
    // active.
    public IEnumerable<TBInfoEntry> Entries => _entries.Values;

    // Reload the store from setName's TBInfo.json. Pass null to clear. Safe to
    // call repeatedly. Wired by AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        // Empty at once, as a store being reloaded always was; the new entries are
        // filled apart and put in place whole.
        _entries = new Dictionary<int, TBInfoEntry>();
        Dictionary<int, TBInfoEntry> entries = new();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "TBInfo", "No active game-data set; TBInfo cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        GameTable? doc = _cache.GetTable("TBInfo");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "TBInfo",
                $"Active set '{setName}' has no TBInfo.json; store is empty.");
            StoreReloaded?.Invoke();
            return;
        }

        int parsed = 0;
        int skipped = 0;
        foreach (GameRow row in doc.Rows)
        {
            if (!TryReadEntry(row, out TBInfoEntry? entry))
            {
                skipped++;
                continue;
            }
            entries[entry.Number] = entry;
            parsed++;
        }
        _entries = entries;

        _cache.EvictTable("TBInfo");

        _log?.Log(LogSeverity.Info, "TBInfo",
            $"Loaded {parsed} TBInfo entry(ies) from '{setName}'"
            + (skipped > 0 ? $" ({skipped} malformed row(s) skipped)." : "."));

        StoreReloaded?.Invoke();
    }

    private static bool TryReadEntry(GameRow row, out TBInfoEntry entry)
    {
        entry = null!;

        if (!row.TryGetProperty("Number", out GameValue nEl)
            || nEl.ValueKind != JsonValueKind.Number
            || !nEl.TryGetInt32(out int number)
            || number <= 0)
            return false;

        int linkTo = 0;
        if (row.TryGetProperty("LinkTo", out GameValue linkEl)
            && linkEl.ValueKind == JsonValueKind.Number)
            linkEl.TryGetInt32(out linkTo);

        string? action = null;
        if (row.TryGetProperty("Action", out GameValue actEl)
            && actEl.ValueKind == JsonValueKind.String)
        {
            action = actEl.GetString();
            // MDB encodes empty as "\0" — normalise to null so consumers
            // don't carry a sentinel value through the directive parser.
            if (!string.IsNullOrEmpty(action) && action.Length == 1 && action[0] == '\0')
                action = null;
        }

        string? calledFrom = null;
        if (row.TryGetProperty("Called From", out GameValue cfEl)
            && cfEl.ValueKind == JsonValueKind.String)
            calledFrom = cfEl.GetString();

        entry = new TBInfoEntry
        {
            Number = number,
            LinkTo = linkTo,
            Action = action,
            CalledFrom = calledFrom,
        };
        return true;
    }
}
