using System.Globalization;

namespace MudPlay.Services;

// Samples the process's memory footprint to its own rolling on-disk file so an
// all-night session leaves a trail of how memory moved over time. That trail is
// what tells a real managed-heap leak (the GC heap climbs sample over sample)
// apart from working-set creep (the OS working set grows while the managed heap
// stays flat — the runtime holding pages it hasn't returned, not a leak).
//
// The per-generation split (committed / gen2-size / loh-size / loh-frag) further
// separates the two look-alikes that both read as "ws climbing": a Large-Object-
// Heap fragmentation ratchet (committed climbs and loh-frag is high while
// gen2-size stays flat — workstation GC holding decommitted LOH pages) vs. a
// genuine gen2 leak (gen2-size itself climbs sample over sample). Without the
// split the raw gc-heap number can't tell them apart.
//
// A native leak shows in none of those: private bytes climb into the gigabytes
// over a flat managed heap. So each sample also carries the native figure
// (private bytes less GC committed) and what can hold native memory (threads, OS
// and GC handles, timers, GDI / USER objects on Windows, Skia's caches), the
// windows open and what they hold, and how much work went by since the last
// sample, so a climb can be set beside what the client was doing.
//
// Deliberately kept OUT of the program log: a sample a minute all night would
// bury the operator-facing entries, and these numbers are only interesting when
// chasing memory. It lands in its own Logs/{ts}-memory.log instead, so the
// program log stays clean while the memory history is a file away.
//
// Gated by LogDiagnosticState.AutoCollectLogs: the timer always ticks, but each
// sample is dropped while the flag is off (the default), so a normal session
// writes no file. Flipping the Log-pane "Auto-collect logs" toggle on opens a
// fresh file with the header + a t0 baseline sample; off closes it. One
// instance per app session, instantiated once in AppServices.
public sealed class MemoryUsageLog : IAsyncDisposable
{
    // A sample a minute keeps an all-night file tiny (~500 lines over 8 h) while
    // still resolving a slow crawl.
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] ColumnLines =
    [
        "# columns: working-set private managed-heap gc-heap committed fragmented "
            + "gen2-size loh-size loh-frag poh-size gen0/1/2-collections",
        "# then: native (private bytes less GC committed, not the managed heap) threads, OS handles, "
            + "GC handles (heard only after a collection), pinned objects, thread-pool threads, timers, "
            + "gdi/user objects (Windows), skia resource cache used/limit, font cache used/limit:entries",
        "# then: since the last sample, lines received, terminal renders, map redraws and native growth; "
            + "then what the windows hold, and the windows open",
    ];

    // One sample's figures.
    public sealed record Sample(HangReport.Memory Memory, ProcessResources Resources, string Windows, string Contents,
        ActivityCounters.Counts Activity);

    // Where the log reads its figures and time. In the client: the GC and the
    // process, PerformanceMonitor's windows, ContentSizes, ActivityCounters, the
    // wall clock and a thread-pool timer. Arm: run the action once after the delay.
    internal sealed record Sources(
        Func<Sample> Read,
        Func<LogFileHeader.Session> Session,
        Func<DateTimeOffset> Now,
        Func<TimeSpan, Action, IDisposable> Arm);

    private readonly LogDiagnosticState _diagnostics;
    private readonly Sources _sources;

    // Guards the writer, the timer and the previous sample against the sampling
    // threadpool thread racing a toggle-driven open/close.
    private readonly object _gate = new();
    private DebugLogWriter? _writer;
    private IDisposable? _timer, _baseline;
    private bool _broken, _disposed;
    private (DateTimeOffset At, Sample Sample)? _previous;
    private string? _sessionLine;
    private volatile string? _latest;

    public MemoryUsageLog(LogDiagnosticState diagnostics, PerformanceMonitor performance, ContentSizes contents,
        ActivityCounters activity, Func<LogFileHeader.Session> session)
        : this(diagnostics, new Sources(
            () =>
            {
                ProcessResources resources = ProcessResources.Read(performance.GcHandleCount, renderer: true);
                return new Sample(HangReport.Memory.Read(resources.PrivateBytes), resources,
                    performance.OpenWindowsSummary, contents.Describe(), activity.Read());
            },
            session,
            () => DateTimeOffset.Now,
            (delay, tick) => new System.Threading.Timer(_ => tick(), null, delay, Timeout.InfiniteTimeSpan)))
    {
    }

    internal MemoryUsageLog(LogDiagnosticState diagnostics, Sources sources)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _diagnostics.Changed += OnDiagnosticsChanged;
        // The timer runs for the whole session; a tick no-ops while the writer is
        // closed, so the cost of collection is only paid when AutoCollectLogs is on.
        lock (_gate) _timer = _sources.Arm(SampleInterval, Tick);
        SyncWriter();
    }

    // True while the on-disk file is open and receiving samples.
    public bool IsCollecting
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    // The file being written, or null while not collecting.
    public string? CurrentPath
    {
        get { lock (_gate) { return _writer?.Path; } }
    }

    // The last sample's headline ("native 812.4MB, private 1290.0MB at 14:02"),
    // for the client's status line; null while not collecting. Never waits.
    public string? LatestSummary => _latest;

    // Open or close the writer to match AutoCollectLogs. On open, write the
    // header and take an immediate t0 baseline so the file has a datum before
    // the first interval elapses. Once _broken we stay closed for the session.
    private void SyncWriter()
    {
        lock (_gate)
        {
            if (_broken || _disposed) return;
            bool want = _diagnostics.AutoCollectLogs;
            if (want && _writer is null)
            {
                try
                {
                    LogFileHeader.Session session = LogFileHeader.Read(_sources.Session);
                    _writer = new DebugLogWriter("memory", session, _sources.Now);
                    foreach (string line in ColumnLines) _writer.WriteLine(line);
                    _sessionLine = session.Line;
                    _previous = null;
                    // The t0 baseline, off the toggle's thread (the UI thread): a
                    // sample reads the process's threads and handles, which on
                    // Windows walks every process on the machine. Held so the
                    // timer isn't collected before it fires.
                    _baseline?.Dispose();
                    _baseline = _sources.Arm(TimeSpan.Zero, TakeSample);
                }
                catch { _broken = true; }
            }
            else if (!want && _writer is not null)
            {
                _writer.Dispose();
                _writer = null;
                _latest = null;
            }
        }
    }

    private void OnDiagnosticsChanged() => SyncWriter();

    // On the timer's thread: a sample, then the next tick.
    private void Tick()
    {
        TakeSample();
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = _sources.Arm(SampleInterval, Tick);
        }
    }

    // The _gate lock guards the writer swap, so no marshalling is needed. No-op
    // while the writer is closed.
    private void TakeSample()
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                DateTimeOffset now = _sources.Now();
                Sample sample = _sources.Read();
                // A character loaded or swapped since the header: the file says
                // whose session the samples after it belong to.
                string session = LogFileHeader.Read(_sources.Session).Line;
                if (!string.Equals(session, _sessionLine, StringComparison.Ordinal))
                {
                    _writer.WriteLine("# now " + session);
                    _sessionLine = session;
                }
                _writer.WriteLine(Format(sample, _previous is { } p ? (now - p.At, p.Sample) : null));
                _previous = (now, sample);
                _latest = $"native {ProcessResources.Mb(sample.Resources.NativeBytes)}, "
                    + $"private {ProcessResources.Mb(sample.Resources.PrivateBytes)} at {now:HH:mm}";
            }
            catch
            {
                // Disk full / handle lost / permission flap. Stop sampling and stay
                // silent: losing the trail is acceptable, wedging on it is not.
                _broken = true;
                _writer.Dispose();
                _writer = null;
                _latest = null;
            }
        }
    }

    // One sample line; since: the time and figures of the sample before it, for
    // the rates, or null for the file's first.
    private static string Format(Sample s, (TimeSpan Elapsed, Sample Sample)? since)
    {
        HangReport.Memory m = s.Memory;
        ProcessResources r = s.Resources;
        string line =
            $"ws={Mb(m.WorkingSet)} priv={ProcessResources.Mb(r.PrivateBytes)} "
            + $"managed={Mb(m.Heap)} heap={Mb(m.GcHeapSize)} "
            + $"committed={Mb(r.GcCommitted)} frag={Mb(r.Fragmented)} "
            + $"gen2sz={Mb(r.Gen2Size)} loh={Mb(r.LohSize)} lohfrag={Mb(r.LohFragmented)} poh={Mb(r.PohSize)} "
            + $"gc0={m.Gen0} gc1={m.Gen1} gc2={m.Gen2} "
            + r.Columns();
        if (since is { } prev)
        {
            ActivityCounters.Counts d = s.Activity.Since(prev.Sample.Activity);
            string native = r.NativeBytes is { } now && prev.Sample.Resources.NativeBytes is { } then
                ? (now >= then ? "+" : "-") + Mb(Math.Abs(now - then))
                : "?";
            line += $" | in {prev.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s: "
                + $"lines+{d.Lines} renders+{d.Renders} mapdraws+{d.MapRedraws} native{native}";
        }
        return line + $" | {s.Contents} | windows {s.Windows}";
    }

    private static string Mb(long bytes) => ProcessResources.Mb(bytes);

    public async ValueTask DisposeAsync()
    {
        _diagnostics.Changed -= OnDiagnosticsChanged;
        DebugLogWriter? writer;
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _baseline?.Dispose();
            _baseline = null;
            writer = _writer;
            _writer = null;
        }
        if (writer is not null) await writer.DisposeAsync();
    }
}
