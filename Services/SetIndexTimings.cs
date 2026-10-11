using System.Diagnostics;
using System.Globalization;

namespace MudPlay.Services;

// What each per-set index took to rebuild in one load of a set, for the one program
// log line that says so: a "the switch froze the window" report can tell from it
// whether these indexes were the time, and which of them.
//
// The rebuilds run one after another on the thread that switches the set, so the
// list needs no lock. It belongs to one load: a rebuild that throws ends that load's
// handlers before the line is written, and the times it left must not turn up in the
// next load's line, so each timed rebuild first checks that the list is its load's.
internal sealed class SetIndexTimings
{
    private readonly GameDataCache _cache;
    private readonly LogService _log;
    private readonly List<(string Name, double Ms)> _times = new();
    private GameDataCache.Load _load;

    public SetIndexTimings(GameDataCache cache, LogService log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(log);
        _cache = cache;
        _log = log;
    }

    // rebuild as an ActiveSetChanged handler that records how long it took.
    public Action<string?> Timed(string name, Action<string?> rebuild) => set =>
    {
        GameDataCache.Load load = _cache.ActiveLoad;
        if (load != _load)
        {
            _times.Clear();
            _load = load;
        }
        long started = Stopwatch.GetTimestamp();
        rebuild(set);
        _times.Add((name, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    };

    // Subscribed after the last timed rebuild: writes the line and empties the list.
    public void LogLoad(string? set)
    {
        if (set is not null && _times.Count > 0 && _load == _cache.ActiveLoad)
        {
            static string Ms(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);
            _log.Info("GameData",
                $"Per-set indexes for '{set}' rebuilt in {Ms(_times.Sum(t => t.Ms))} ms on the switching thread: "
                + string.Join(", ", _times.OrderByDescending(t => t.Ms).Select(t => $"{t.Name} {Ms(t.Ms)}")) + ".");
        }
        _times.Clear();
    }
}
