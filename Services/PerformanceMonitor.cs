using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MudPlay.Services;

// How smoothly the UI thread runs, measured in the user's own sessions: every
// stutter (a burst of output, a window open, a save) is a stretch of time the UI
// thread spent on something other than painting and input. The numbers land in
// the bug report's Performance section and long stalls in the program log, so a
// report says what was slow, how often and how badly, instead of "it felt laggy".
//
// Stalls are found by a ping: every PingInterval a watcher thread posts a no-op at
// the lowest dispatcher priority, which runs only once the UI thread has caught up
// on everything ahead of it. The wait before it runs is the stall. While a ping is
// overdue the watcher samples what the UI thread said it was doing (Measure), and
// a window that opened during the stall is named too, so a stall carries its
// likely cause.
//
// Measure also times the work items that matter (terminal feed per network
// chunk, profile saves, game-data parses, renders) into per-name summaries.
public sealed class PerformanceMonitor : IDisposable
{
    public const double StallMs = 50;
    // Stalls are counted from StallMs, but only one this long reaches the program
    // log: a 60 ms hitch in a burst of output is worth a count, not a line.
    public const double LogStallMs = 100;
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LogGap = TimeSpan.FromSeconds(5);
    private const int RecentStallCount = 25;
    private const int SamplesPerTiming = 512;

    private readonly LogService? _log;
    private readonly Action<Action>? _postLowPriority;
    private readonly Func<bool> _onUiThread;
    private readonly Thread? _watcher;
    private volatile bool _stopped;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly long _startAllocated = GC.GetTotalAllocatedBytes();
    private readonly TimeSpan _startPause = GC.GetTotalPauseDuration();

    // What the UI thread is doing, set by Measure on the UI thread and read by the
    // watcher. A plain reference write is atomic; volatile keeps it fresh.
    private volatile string? _activity;

    // Stopwatch timestamp of the ping in flight; 0 when none is.
    private long _pingPostedAt;
    private long _lastPingAt;
    // What the watcher saw the UI thread doing while the ping in flight was late.
    private readonly HashSet<string> _seenDuringPing = new();
    private (string Name, long At)? _lastWindowOpened;
    private readonly object _pingLock = new();

    private readonly object _statsLock = new();
    private readonly Dictionary<string, Timing> _timings = new(StringComparer.Ordinal);
    private readonly Queue<Stall> _recentStalls = new();
    private int _stalls, _stalls250, _stalls1000;
    private double _stalledMs;
    private double _worstStallMs;
    private DateTime _lastStallLogUtc = DateTime.MinValue;
    private int _unloggedStalls;

    private sealed record Stall(DateTime AtUtc, double Ms, string Cause);

    // The production monitor: pings the Avalonia dispatcher from its own thread and
    // names windows as they open.
    public PerformanceMonitor(LogService? log)
        : this(log, a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background),
               () => Dispatcher.UIThread.CheckAccess(), startWatcher: true)
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>(
            (window, _) => NoteWindowOpened(window.GetType().Name), RoutingStrategies.Direct);
    }

    // Tests drive stalls and timings directly, without a watcher or dispatcher.
    internal PerformanceMonitor(LogService? log, Action<Action>? postLowPriority, Func<bool> onUiThread, bool startWatcher)
    {
        _log = log;
        _postLowPriority = postLowPriority;
        _onUiThread = onUiThread;
        if (!startWatcher || postLowPriority is null) return;
        _watcher = new Thread(Watch) { IsBackground = true, Name = "UI stall probe", Priority = ThreadPriority.AboveNormal };
        _watcher.Start();
    }

    // Times a piece of work under name. On the UI thread it is also what a stall
    // seen meanwhile is put down to. Nested scopes restore the outer name.
    public Scope Measure(string name)
    {
        bool onUi = _onUiThread();
        string? outer = null;
        if (onUi)
        {
            outer = _activity;
            _activity = name;
        }
        return new Scope(this, name, onUi, outer, Stopwatch.GetTimestamp());
    }

    public readonly struct Scope : IDisposable
    {
        private readonly PerformanceMonitor? _monitor;
        private readonly string _name;
        private readonly bool _onUi;
        private readonly string? _outer;
        private readonly long _start;

        internal Scope(PerformanceMonitor monitor, string name, bool onUi, string? outer, long start)
        {
            _monitor = monitor;
            _name = name;
            _onUi = onUi;
            _outer = outer;
            _start = start;
        }

        public void Dispose()
        {
            if (_monitor is null) return;
            _monitor.Record(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
            if (_onUi) _monitor._activity = _outer;
        }
    }

    internal void Record(string name, double ms)
    {
        lock (_statsLock)
        {
            if (!_timings.TryGetValue(name, out Timing? timing))
            {
                timing = new Timing();
                _timings[name] = timing;
            }
            timing.Add(ms);
        }
    }

    private void NoteWindowOpened(string name)
    {
        lock (_pingLock) _lastWindowOpened = (name, Stopwatch.GetTimestamp());
    }

    private void Watch()
    {
        while (!_stopped)
        {
            Thread.Sleep(WatchInterval);
            long now = Stopwatch.GetTimestamp();
            long posted = Interlocked.Read(ref _pingPostedAt);
            if (posted == 0)
            {
                if (Stopwatch.GetElapsedTime(_lastPingAt, now) < PingInterval) continue;
                _lastPingAt = now;
                Interlocked.Exchange(ref _pingPostedAt, now);
                _postLowPriority!(OnPing);
            }
            else if (Stopwatch.GetElapsedTime(posted, now).TotalMilliseconds >= StallMs
                     && _activity is { } doing)
            {
                lock (_pingLock) _seenDuringPing.Add(doing);
            }
        }
    }

    private void OnPing()
    {
        long posted = Interlocked.Exchange(ref _pingPostedAt, 0);
        if (posted == 0) return;
        double ms = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
        List<string> causes;
        lock (_pingLock)
        {
            causes = _seenDuringPing.ToList();
            _seenDuringPing.Clear();
            if (_lastWindowOpened is { } opened && opened.At >= posted)
                causes.Add($"opening {opened.Name}");
        }
        if (ms >= StallMs) RecordStall(ms, causes.Count == 0 ? "unattributed" : string.Join(", ", causes));
    }

    internal void RecordStall(double ms, string cause)
    {
        string? note = null;
        lock (_statsLock)
        {
            _stalls++;
            if (ms >= 250) _stalls250++;
            if (ms >= 1000) _stalls1000++;
            _stalledMs += ms;
            _worstStallMs = Math.Max(_worstStallMs, ms);
            _recentStalls.Enqueue(new Stall(DateTime.UtcNow, ms, cause));
            while (_recentStalls.Count > RecentStallCount) _recentStalls.Dequeue();

            if (ms < LogStallMs) return;
            DateTime now = DateTime.UtcNow;
            if (now - _lastStallLogUtc < LogGap)
            {
                _unloggedStalls++;
                return;
            }
            note = $"UI thread stalled {ms:0} ms ({cause})"
                + (_unloggedStalls > 0 ? $"; {_unloggedStalls} more over {LogStallMs:0} ms since the last note" : "");
            _lastStallLogUtc = now;
            _unloggedStalls = 0;
        }
        _log?.Info("Performance", note);
    }

    // The bug report's Performance section.
    public string Describe()
    {
        StringBuilder sb = new();
        TimeSpan up = DateTime.UtcNow - _startedUtc;
        lock (_statsLock)
        {
            sb.Append("- **Measured for:** ").Append(Duration(up)).Append('\n');
            sb.Append("- **UI stalls:** ").Append(_stalls).Append(" over ").Append(StallMs.ToString("0", CultureInfo.InvariantCulture))
              .Append(" ms (").Append(_stalls250).Append(" over 250 ms, ").Append(_stalls1000).Append(" over 1 s); ")
              .Append("worst ").Append(_worstStallMs.ToString("0", CultureInfo.InvariantCulture)).Append(" ms; ")
              .Append("stalled ").Append((_stalledMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s in all\n");
            sb.Append('\n');
            if (_recentStalls.Count > 0)
            {
                sb.Append("Recent stalls (newest last):\n\n");
                foreach (Stall stall in _recentStalls)
                    sb.Append("- ").Append(stall.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                      .Append("  ").Append(stall.Ms.ToString("0", CultureInfo.InvariantCulture)).Append(" ms  ")
                      .Append(stall.Cause).Append('\n');
                sb.Append('\n');
            }
            if (_timings.Count > 0)
            {
                sb.Append("| Work | Count | p50 ms | p95 ms | Max ms | Total s |\n|---|---|---|---|---|---|\n");
                foreach ((string name, Timing t) in _timings.OrderByDescending(kv => kv.Value.TotalMs))
                {
                    (double p50, double p95) = t.Percentiles();
                    sb.Append("| ").Append(name).Append(" | ").Append(t.Count)
                      .Append(" | ").Append(p50.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(" | ").Append(p95.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(" | ").Append(t.MaxMs.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(" | ").Append((t.TotalMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)).Append(" |\n");
                }
                sb.Append('\n');
            }
        }

        using Process process = Process.GetCurrentProcess();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        double seconds = Math.Max(1, up.TotalSeconds);
        double allocatedMb = (GC.GetTotalAllocatedBytes() - _startAllocated) / (1024.0 * 1024.0);
        sb.Append("- **CPU:** ").Append((process.TotalProcessorTime.TotalSeconds / Math.Max(1, (DateTime.Now - process.StartTime).TotalSeconds) * 100)
              .ToString("0.0", CultureInfo.InvariantCulture)).Append("% of one core on average since start; ")
          .Append(process.Threads.Count).Append(" threads\n");
        sb.Append("- **Memory:** working set ").Append(Mb(process.WorkingSet64)).Append(", private ").Append(Mb(process.PrivateMemorySize64))
          .Append(", GC heap ").Append(Mb(gc.HeapSizeBytes)).Append(", GC committed ").Append(Mb(gc.TotalCommittedBytes)).Append('\n');
        sb.Append("- **GC:** ").Append(GC.CollectionCount(0)).Append(" / ").Append(GC.CollectionCount(1)).Append(" / ")
          .Append(GC.CollectionCount(2)).Append(" collections (gen 0 / 1 / 2); paused ")
          .Append((GC.GetTotalPauseDuration() - _startPause).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture))
          .Append(" ms in all; allocating ").Append((allocatedMb / seconds).ToString("0.00", CultureInfo.InvariantCulture)).Append(" MB/s on average\n");
        return sb.ToString();
    }

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";

    public void Dispose() => _stopped = true;

    // A running count, total and maximum, plus the latest SamplesPerTiming times
    // for the percentiles.
    private sealed class Timing
    {
        private readonly double[] _samples = new double[SamplesPerTiming];
        private int _next;
        public int Count { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }

        public void Add(double ms)
        {
            _samples[_next] = ms;
            _next = (_next + 1) % _samples.Length;
            Count++;
            TotalMs += ms;
            MaxMs = Math.Max(MaxMs, ms);
        }

        public (double P50, double P95) Percentiles()
        {
            int n = Math.Min(Count, _samples.Length);
            if (n == 0) return (0, 0);
            double[] sorted = _samples[..n];
            Array.Sort(sorted);
            return (sorted[(int)Math.Round(0.50 * (n - 1))], sorted[(int)Math.Round(0.95 * (n - 1))]);
        }
    }
}
