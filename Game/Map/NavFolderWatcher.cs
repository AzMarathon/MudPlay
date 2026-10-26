using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps this client's loops and lair setups in step with the set's Loops folder, which
// every client running on the set shares. Driven by the 1 s heartbeat; the folder is
// large (hundreds of files) and changes seldom, so the tree is walked only when there
// is reason to think it changed:
//
// - The system tells of changes (FileSystemWatcher). A change wakes the watcher, which
//   waits for a beat with nothing new, so a burst of saves is walked once, then walks;
//   a change seen is confirmed by a second walk on the next beat and read only when the
//   two agree. A change made here or in another client shows in two to four seconds.
//   Under changes that never stop (a sync tool, a long copy) it walks anyway after
//   MaxSettleBeats, and on a check's beat.
// - Every minute a cheap check (NavTreeScan.Signature: no string per file) compares the
//   whole tree with the last check and walks only when it differs. It is what finds a
//   change the system never reports: a network share edited from another machine,
//   events lost to an overflow, a watcher that could not be started (the system's
//   watch limit reached) or that stopped. Those changes show within about a minute.
// - A set newly taken up is walked on the next beat, so a change made between the
//   catalogues' load and the first check is never taken in as how the folder was.
// - Everything that touches the folder runs off the UI thread (a share that is slow
//   or down would freeze the client): the walk, the check, the file reads and starting
//   the system watcher. Only their results come back to the UI thread, where comparing
//   a walk with what each catalogue holds is a few hundred lookups; the swap of a
//   catalogue is the only part that runs there. One walk serves both catalogues, one at
//   a time.
// - Everything this client writes bumps a generation count. A walk or a read that
//   began before a bump may have seen its own write half done, so its result is
//   dropped, as is one that returns after the set changed. (A result that returns
//   after the client closes is posted to a dispatcher that no longer runs.) The check
//   keeps its signature only once a walk has found the tree and the catalogues
//   agreeing, so a change whose walk was dropped is walked again at the next check. A
//   watcher that finishes starting after the set changed is stopped unused.
//
// A tree that can't be walked (a share that is down, a drive taken out) says nothing
// about what is in it: the catalogues keep what they hold. A root folder that went
// away takes the watcher with it; the check starts it again once the folder is back.
public sealed class NavFolderWatcher : IDisposable
{
    // Beats (seconds) between two checks of the whole tree.
    private const int CheckEveryBeats = 60;
    // Beats a run of told changes may hold off the walk.
    private const int MaxSettleBeats = 5;

    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly NavFolderManager _folders;
    private readonly LogService? _log;
    private readonly int _checkEvery;
    private readonly Func<string, Task<NavTreeScan>> _walk;
    private readonly Func<string, Task<long?>> _signature;
    private readonly Func<Func<Action>, Task<Action>> _read;
    private readonly Action<Action> _post;
    private readonly Func<string, Action, Action, Task<IDisposable>> _startWatching;

    private Task _inFlight = Task.CompletedTask;
    private Task _watchStarting = Task.CompletedTask;
    private int _beats;
    private NavTreeScan? _awaitingConfirmation;
    private string? _lastSet;
    private bool _rootSeen;
    private bool _rootWarned;
    private bool _disposed;

    private IDisposable? _watching;
    private string? _watchingNotes;
    // Bumped whenever the watcher is stopped: a start begun before it is stale.
    private long _watchEpoch;
    private bool _startingWatch;
    // Set from the system's own thread when it tells of a change, or of having lost
    // track (an overflow, the folder gone); read and cleared on the heartbeat.
    private int _changeTold;
    private int _watchBroken;
    // A change was told on an earlier beat; walk on the first beat that brings none.
    private bool _settling;
    private int _settledBeats;
    // The tree as the last check saw it, kept once a walk found the catalogues agreeing
    // with it. Null makes the next check walk.
    private long? _lastSignature;
    private long _lastCheckTicks;

    public NavFolderWatcher(LoopManager loops, LairManager lairs, NavFolderManager folders, LogService? log = null)
        : this(loops, lairs, folders, log, CheckEveryBeats, walk: null, signature: null, read: null, post: null, startWatching: null)
    {
    }

    // The tree walk, the check, the file reads and starting the system watcher run off
    // the UI thread, and what they found is posted back to the UI thread, where the
    // catalogues live. Each hop is a parameter; the public constructor passes the
    // thread pool, the dispatcher and a FileSystemWatcher started on the pool.
    internal NavFolderWatcher(LoopManager loops, LairManager lairs, NavFolderManager folders, LogService? log,
        int checkEvery, Func<string, Task<NavTreeScan>>? walk, Func<string, Task<long?>>? signature,
        Func<Func<Action>, Task<Action>>? read, Action<Action>? post, Func<string, Action, Action, Task<IDisposable>>? startWatching)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(folders);
        _loops = loops;
        _lairs = lairs;
        _folders = folders;
        _log = log;
        _checkEvery = checkEvery;
        _walk = walk ?? (root => Task.Run(() => NavTreeScan.Walk(root, LoopManager.LoopFileSuffix, LairManager.LairFileSuffix)));
        _signature = signature ?? (root => Task.Run(() => NavTreeScan.Signature(root, LoopManager.LoopFileSuffix, LairManager.LairFileSuffix)));
        _read = read ?? (job => Task.Run(job));
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _startWatching = startWatching ?? ((root, changed, broken) => Task.Run(() => WatchFolder(root, changed, broken)));
    }

    // The walk and read now under way, or the last one; completed when none is.
    internal Task InFlight => _inFlight;

    // Starting the system watcher, or the last start; completed when none is under way.
    internal Task WatchStarting => _watchStarting;

    // How the folder is followed and when it was last checked or walked, for the bug
    // report. UI thread.
    public string Mode => _lastSet is null ? "no game-data set"
        : _watching is not null ? $"change notification, checked every {_checkEvery} s"
        : $"checked every {_checkEvery} s ({_watchingNotes ?? "change notification not started yet"})";

    // Written by the check on its worker thread.
    public DateTimeOffset? LastCheckAt => Interlocked.Read(ref _lastCheckTicks) is long t and > 0
        ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public DateTimeOffset? LastWalkAt { get; private set; }

    // One heartbeat. UI thread.
    public void OnHeartbeat()
    {
        if (_disposed || !_inFlight.IsCompleted) return;
        string? set = _loops.SetName;
        if (set != _lastSet)
        {
            _lastSet = set;
            _awaitingConfirmation = null;
            _rootSeen = false;
            _rootWarned = false;
            _settling = set is not null;
            _settledBeats = 0;
            _lastSignature = null;
            _beats = 0;
            _folders.ForgetBaseline();
            StopWatching();
            if (set is not null) StartWatching(set);
        }
        if (set is null) return;
        _beats++;
        string root = AppPaths.GameDataSetLoopsFolder(set);
        bool checkDue = _beats % _checkEvery == 0;

        if (Interlocked.Exchange(ref _watchBroken, 0) == 1 && _watching is not null)
        {
            StopWatching();
            _watchingNotes = "change notification stopped; restarted at the next check";
            _log?.Info("Loops", $"change notification for the Loops folder of set '{set}' stopped; checking it every {_checkEvery} s until it can be restarted.");
            _settling = true;
        }
        bool told = Interlocked.Exchange(ref _changeTold, 0) == 1;
        if (told)
        {
            _settling = true;
            _settledBeats++;
        }
        if (_awaitingConfirmation is not null)
        {
            _inFlight = PollAsync(set, root, Generation(), checkFirst: false, known: null);
            return;
        }
        if (_settling && (!told || _settledBeats >= MaxSettleBeats || checkDue))
        {
            _settling = false;
            _settledBeats = 0;
            _log?.Debug("Loops", $"Loops folder of set '{set}' changed on disk, or was just taken up; walking it.");
            if (checkDue && _watching is null) StartWatching(set);
            _inFlight = PollAsync(set, root, Generation(), checkFirst: false, known: null);
            return;
        }
        if (told || !checkDue) return;
        if (_watching is null) StartWatching(set);
        _inFlight = PollAsync(set, root, Generation(), checkFirst: true, known: _lastSignature);
    }

    private long Generation() => _loops.Generation + _lairs.Generation + _folders.Generation;

    // UI thread. The watcher starts on a worker; what it made comes back here and is
    // kept only if nothing stopped the watcher meanwhile (a set switch, a break).
    private void StartWatching(string set)
    {
        if (_startingWatch) return;
        _startingWatch = true;
        _watchStarting = StartWatchingAsync(set, AppPaths.GameDataSetLoopsFolder(set), _watchEpoch);
    }

    private async Task StartWatchingAsync(string set, string root, long epoch)
    {
        IDisposable? started = null;
        Exception? failed = null;
        try
        {
            started = await _startWatching(root,
                () => Interlocked.Exchange(ref _changeTold, 1),
                () => Interlocked.Exchange(ref _watchBroken, 1)).ConfigureAwait(false);
        }
        // Whatever stopped it (the folder not made yet, the system's watch limit
        // reached, a file system with no notification), the minute's check covers it
        // and the watcher is tried again at each check.
        catch (Exception ex)
        {
            failed = ex;
        }
        await OnUiThread(() =>
        {
            if (_disposed || epoch != _watchEpoch || set != _lastSet)
            {
                started?.Dispose();
                return 0;
            }
            _startingWatch = false;
            if (started is not null)
            {
                _watching = started;
                _watchingNotes = null;
                _log?.Info("Loops", $"following the Loops folder of set '{set}' by change notification, with a check of the whole tree every {_checkEvery} s.");
                return 0;
            }
            string notes = $"change notification unavailable: {failed?.Message}";
            if (notes != _watchingNotes)
                _log?.Info("Loops", $"following the Loops folder of set '{set}' by a check of the whole tree every {_checkEvery} s ({notes}).");
            _watchingNotes = notes;
            return 0;
        }).ConfigureAwait(false);
    }

    private void StopWatching()
    {
        _watchEpoch++;
        _startingWatch = false;
        _watching?.Dispose();
        _watching = null;
        _watchingNotes = null;
        Interlocked.Exchange(ref _changeTold, 0);
        Interlocked.Exchange(ref _watchBroken, 0);
    }

    // The whole tree under root, every kind of change that can alter a walk: names of
    // files and folders (an add, a delete, a rename, a move), a write, a size. The
    // events arrive on the system's own thread and only raise a flag. Run on a worker
    // thread: the folder may be on a share that is down.
    private static IDisposable WatchFolder(string root, Action changed, Action broken)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"'{root}' does not exist yet");
        FileSystemWatcher watcher = new(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            // The most Windows allows for a network folder: a save burst across
            // hundreds of files fits without an overflow.
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, _) => changed();
        watcher.Created += (_, _) => changed();
        watcher.Deleted += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.Error += (_, _) => broken();
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        return watcher;
    }

    // checkFirst: compare the tree's signature with known first, and walk only when it
    // differs. Otherwise walk at once (a change was told, or one awaits confirmation).
    private async Task PollAsync(string set, string root, long generation, bool checkFirst, long? known)
    {
        try
        {
            long? signature = null;
            if (checkFirst)
            {
                signature = await _signature(root).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastCheckTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (signature is not null && signature == known) return;
                if (known is not null)
                    _log?.Debug("Loops", $"the check of the Loops folder of set '{set}' found it changed; walking it.");
            }
            NavTreeScan scan = await _walk(root).ConfigureAwait(false);
            bool rootGone = !scan.Complete && !Directory.Exists(root);
            Func<Action>? job = await OnUiThread(() => Judge(scan, set, generation, signature, rootGone)).ConfigureAwait(false);
            if (job is null) return;
            Action swap = await _read(job).ConfigureAwait(false);
            await OnUiThread(() =>
            {
                if (Current(set, generation)) swap();
                else _awaitingConfirmation = null;
                return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn("Loops", $"following the Loops folder failed: {ex.Message}");
        }
    }

    private bool Current(string set, long generation) =>
        set == _loops.SetName && generation == Generation();

    // UI thread. What to read now, or null when there is nothing to do yet. signature
    // is the check that led to this walk, kept when the walk finds nothing to do.
    // rootGone: the Loops folder itself is missing, which ends the watcher on it.
    private Func<Action>? Judge(NavTreeScan scan, string set, long generation, long? signature, bool rootGone)
    {
        LastWalkAt = DateTimeOffset.Now;
        _lastSignature = null;
        if (!Current(set, generation))
        {
            _awaitingConfirmation = null;
            return null;
        }
        if (!scan.Complete)
        {
            _awaitingConfirmation = null;
            if (rootGone && _watching is not null && set == _lastSet)
            {
                StopWatching();
                _watchingNotes = "the folder went; watched again once it is back";
                _log?.Info("Loops", $"the Loops folder of set '{set}' went away; change notification stopped until it is back.");
            }
            if (_rootSeen && !_rootWarned)
            {
                _rootWarned = true;
                _log?.Warn("Loops", $"the Loops folder for set '{set}' can't be read; keeping the loops and lair setups held until it is back.");
            }
            return null;
        }
        _rootSeen = true;
        if (_rootWarned)
        {
            _rootWarned = false;
            _log?.Info("Loops", $"the Loops folder for set '{set}' can be read again.");
        }

        bool differs = _loops.DiffersFrom(scan) | _lairs.DiffersFrom(scan) | _folders.DiffersFrom(scan);
        if (!differs)
        {
            _awaitingConfirmation = null;
            _lastSignature = signature;
            return null;
        }
        if (_awaitingConfirmation is null || !_awaitingConfirmation.SameAs(scan))
        {
            _awaitingConfirmation = scan;
            return null;
        }

        _awaitingConfirmation = null;
        Func<Action>? loops = _loops.PlanOutside(scan);
        Func<Action>? lairs = _lairs.PlanOutside(scan);
        return () =>
        {
            Action? swapLoops = loops?.Invoke();
            Action? swapLairs = lairs?.Invoke();
            return () =>
            {
                swapLoops?.Invoke();
                swapLairs?.Invoke();
                if (_folders.DiffersFrom(scan)) _folders.ApplyOutside(scan);
            };
        };
    }

    // Run func on the UI thread and hand its result back to the awaiting walk.
    private Task<T> OnUiThread<T>(Func<T> func)
    {
        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try { done.SetResult(func()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public void Dispose()
    {
        _disposed = true;
        StopWatching();
    }
}
