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
// No user-facing toggle by design: this is invisible hygiene the user shouldn't
// have to think about. Its effect is visible in the *-memory.log (heap /
// committed / loh drop after a run) and each run logs at Debug so an operator
// can confirm it fired.
public sealed class MemoryMaintenance : IAsyncDisposable
{
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(5);
    // Coalesce a burst of load work — the import plus each index rebuild all fire
    // ActiveSetChanged — into a single compaction once the dust settles.
    private static readonly TimeSpan LoadSettleDelay = TimeSpan.FromSeconds(5);

    private readonly LogService _log;
    private readonly GameDataCache _gameData;
    private readonly Func<bool> _isCombatActive;
    private readonly System.Threading.Timer _trimTimer;
    private readonly System.Threading.Timer _loadSettleTimer;
    private readonly object _gate = new();
    // A runtime without glibc's malloc_trim (musl / Alpine) throws once on the
    // missing entry point; latch it so we stop probing for the session.
    private bool _mallocTrimUnavailable;
    private bool _disposed;

    public MemoryMaintenance(LogService log, GameDataCache gameData, Func<bool> isCombatActive)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _isCombatActive = isCombatActive ?? throw new ArgumentNullException(nameof(isCombatActive));
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        // Both timers run on threadpool threads; the GC/trim work needs no UI
        // thread, and the _gate lock guards the disposal race.
        _loadSettleTimer = new System.Threading.Timer(
            _ => RunPostLoadCompaction(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _trimTimer = new System.Threading.Timer(
            _ => RunPeriodicTrim(), null, TrimInterval, TrimInterval);
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
