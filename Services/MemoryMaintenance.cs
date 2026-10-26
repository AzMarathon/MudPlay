using System.Runtime;
using System.Runtime.InteropServices;

namespace MudPlay.Services;

// Background memory hygiene for a client that runs in loop mode for days or
// months at a stretch. There are two distinct problems with two different
// rhythms, so each gets its own trigger — and neither waits for the app to be
// "idle", because in loop mode it never is.
//
//  - Post-load LOH compaction (one-shot per game-data load). The MDB import
//    parses large JSON tables (>85KB backing arrays land on the Large Object
//    Heap) and each index evicts its raw table after deserializing. Eviction
//    drops the live objects but the freed LOH space stays committed as
//    fragmentation — observed dead-flat at ~125MB for a whole session, i.e. a
//    startup artifact, not steady-state churn. Loop play does not re-fragment
//    the LOH, so there's nothing to compact mid-session; a single compacting
//    collect once a set settles reclaims it. It's fired off ActiveSetChanged so
//    the one stop-the-world pause the whole service ever causes lands while the
//    user is already waiting on the world to load — invisible.
//
//  - Periodic native trim (forever, on a timer). The working set is dominated by
//    NATIVE memory, not the managed heap: Skia's render/glyph caches plus glibc
//    malloc arenas holding freed pages the allocator never handed back to the OS
//    (glibc keeps up to 8×cores arenas, each grown to its high-water and never
//    shrunk). A gentle background collect frees any dead managed objects still
//    pinning native handles, then malloc_trim(0) returns glibc's free pages to
//    the OS. malloc_trim does not suspend managed threads, and the background
//    collect is Optimized (skips itself when unproductive) and skipped outright
//    while a combat round is live — so this runs during active loop play without
//    a perceptible hitch.
//
//  - Idle compaction (when nobody can feel it). The optimized collect above never
//    compacts, so over a long session the large-object heap fills with holes again
//    (17 of 31 MB fragmented after half an hour in one measurement) and the heap
//    keeps committed what it no longer uses. A blocking, compacting, decommitting
//    collect takes it back, at the cost of a stop-the-world pause. It runs only when
//    IdleCompactionPolicy says the client is idle and stable (no fight, no walk,
//    loop or Auto-Lair, no errand, no more than a handful of game lines), sampled on
//    the UI thread's heartbeat where that state lives, or once as the main window is
//    minimized when nothing is fighting or moving. Each run logs at Info with what it
//    gave back and how long it paused the client; a slow one makes the next wait
//    longer.
//
// No user-facing toggle by design: this is invisible hygiene the user shouldn't
// have to think about. Its effect is visible in the *-memory.log (heap /
// committed / loh drop after a run); the trims log at Debug so an operator can
// confirm they fired, and the bug report shows the last compaction.
public sealed class MemoryMaintenance : IAsyncDisposable
{
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(5);
    // Heartbeats (seconds) between two idle samples.
    private const int IdleSampleBeats = 30;
    // Coalesce a burst of load work — the import plus each index rebuild all fire
    // ActiveSetChanged — into a single compaction once the dust settles.
    private static readonly TimeSpan LoadSettleDelay = TimeSpan.FromSeconds(5);

    // The last idle or minimized compaction, for the bug report. CommittedBeforeMb is
    // as of the collection before it (the GC has no figure in between).
    public readonly record struct Compaction(DateTimeOffset At, string Reason, double CommittedBeforeMb,
        double CommittedAfterMb, double PauseMs);

    private readonly LogService _log;
    private readonly GameDataCache _gameData;
    private readonly Func<bool> _isCombatActive;
    private readonly ActivityCounters _activity;
    private readonly IdleCompactionPolicy _policy = new(DateTimeOffset.Now);
    private readonly System.Threading.Timer _trimTimer;
    private readonly System.Threading.Timer _loadSettleTimer;
    private readonly object _gate = new();
    // A runtime without glibc's malloc_trim (musl / Alpine) throws once on the
    // missing entry point; latch it so we stop probing for the session. Read and set
    // from whichever pool thread trims.
    private volatile bool _mallocTrimUnavailable;
    private bool _disposed;
    private Compaction? _lastCompaction;
    // A minimize and an idle sample may both find one due: one compaction at a time.
    private bool _compacting;
    private int _beats;

    public MemoryMaintenance(LogService log, GameDataCache gameData, Func<bool> isCombatActive, ActivityCounters activity)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _isCombatActive = isCombatActive ?? throw new ArgumentNullException(nameof(isCombatActive));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        // The timers run on threadpool threads; the GC/trim work needs no UI
        // thread, and the _gate lock guards the disposal race. Each callback logs
        // what it throws: an exception on a timer thread would end the process.
        _loadSettleTimer = new System.Threading.Timer(
            _ => Guarded("post-load compaction", RunPostLoadCompaction), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _trimTimer = new System.Threading.Timer(
            _ => Guarded("periodic trim", RunPeriodicTrim), null, TrimInterval, TrimInterval);
    }

    public Compaction? LastCompaction
    {
        get { lock (_gate) return _lastCompaction; }
    }

    // The shortest time between two idle compactions now (longer after a slow one).
    public TimeSpan CompactionInterval
    {
        get { lock (_gate) return _policy.Interval; }
    }

    // One heartbeat. UI thread, where busy (a fight, a walk, loop or Auto-Lair running
    // or paused, an errand) is read; every IdleSampleBeats it is a sample for the idle
    // compaction, which then runs on a pool thread.
    public void OnHeartbeat(bool busy)
    {
        if (++_beats % IdleSampleBeats != 0) return;
        bool due;
        lock (_gate)
        {
            if (_disposed) return;
            due = _policy.Sample(DateTimeOffset.Now, busy, _activity.Read().Lines) && !_compacting;
            _compacting |= due;
        }
        if (due) ThreadPool.QueueUserWorkItem(_ => Guarded("idle compaction", () => Compact("idle")));
    }

    // The main window went down; busy as for OnHeartbeat. UI thread; the collect
    // itself runs on a pool thread.
    public void OnMainWindowMinimized(bool busy)
    {
        bool due;
        lock (_gate)
        {
            if (_disposed) return;
            due = !_compacting && _policy.Minimized(DateTimeOffset.Now, busy);
            _compacting |= due;
        }
        if (due)
        {
            ThreadPool.QueueUserWorkItem(_ => Guarded("minimized compaction", () => Compact("minimized")));
            return;
        }
        _log.Debug("Memory", busy
            ? "window minimized while fighting or moving: no compaction"
            : "window minimized: compacted too recently (or compacting now) to compact again");
    }

    private void Guarded(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _log.Warn("Memory", $"{what} failed: {ex.Message}");
        }
    }

    // A blocking collection of every generation that compacts the large-object heap
    // too and decommits what it frees (Aggressive), then the native trim. The pause is
    // the GC's own count of the time it held the threads.
    private void Compact(string reason)
    {
        TimeSpan pause = TimeSpan.Zero;
        double before = 0, after = 0;
        try
        {
            before = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0;
            TimeSpan pausedBefore = GC.GetTotalPauseDuration();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            pause = GC.GetTotalPauseDuration() - pausedBefore;
            after = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0;
            TrimNativeHeap();
        }
        finally
        {
            lock (_gate)
            {
                _compacting = false;
                _policy.Compacted(DateTimeOffset.Now, pause);
                _lastCompaction = new Compaction(DateTimeOffset.Now, reason, before, after, pause.TotalMilliseconds);
            }
        }
        TimeSpan next = CompactionInterval;
        string backOff = pause > IdleCompactionPolicy.PauseBound
            ? $"; over {IdleCompactionPolicy.PauseBound.TotalMilliseconds:0} ms, so the next idle one waits {next.TotalMinutes:0} min"
            : "";
        _log.Info("Memory",
            $"{reason} compaction: GC committed {before:0.0} MB (at the last collection) → {after:0.0} MB, paused {pause.TotalMilliseconds:0} ms{backOff}");
    }

    private void OnActiveSetChanged(string? _)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // (Re)arm the debounce — each set change pushes the compaction out
            // LoadSettleDelay, so a rapid switch sequence compacts once at the end.
            _loadSettleTimer.Change(LoadSettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunPostLoadCompaction()
    {
        lock (_gate) { if (_disposed) return; }

        long before = GC.GetTotalMemory(false);
        // LOH compaction can't run concurrently — it needs a blocking, compacting
        // gen2 collect. The heap is small (~300MB), so stop-the-world is tens of
        // ms, and this only ever fires right after a set load. The double collect
        // with a finalizer drain between reclaims finalizable objects too.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long after = GC.GetTotalMemory(false);

        bool trimmed = TrimNativeHeap();
        _log.Debug("Memory",
            $"post-load compaction: managed heap {Mb(before)}→{Mb(after)}MB" +
            (trimmed ? ", native free pages returned to OS" : ""));
    }

    private void RunPeriodicTrim()
    {
        lock (_gate) { if (_disposed) return; }

        // Free dead managed objects that pin native handles so their backing can
        // be trimmed below. Optimized lets the GC skip a collection it judges
        // unproductive; blocking:false keeps it in the background. Skipped while a
        // combat round is live so it never competes with an attack send — the
        // native trim below is cheap enough to run regardless.
        if (!_isCombatActive())
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

        bool trimmed = TrimNativeHeap();
        if (trimmed)
            _log.Debug("Memory", "periodic native trim returned free pages to the OS");
    }

    // glibc malloc_trim(0): release free memory from the top of the main arena
    // and madvise the others, handing pages back to the OS. Returns true only
    // when memory was actually released. Linux/glibc only; a silent no-op
    // elsewhere. Mirrors ProcessTitle's classic-DllImport interop style.
    private bool TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux() || _mallocTrimUnavailable) return false;
        try
        {
            return malloc_trim(0) == 1;
        }
        // musl has no malloc_trim; a missing library/entry point is permanent.
        catch (DllNotFoundException) { _mallocTrimUnavailable = true; return false; }
        catch (EntryPointNotFoundException) { _mallocTrimUnavailable = true; return false; }
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        await _loadSettleTimer.DisposeAsync();
        await _trimTimer.DisposeAsync();
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint pad);
}
