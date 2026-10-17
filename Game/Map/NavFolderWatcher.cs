using System.Threading.Tasks;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps this client's loops and lair setups in step with the set's Loops folder, which
// every client running on the set shares. Driven by the 1 s heartbeat; the folder is
// large (hundreds of files), so the work is arranged to stay off the thread that draws
// the terminal:
//
// - One walk of the tree serves both catalogues, and it runs on a worker thread, one
//   at a time. Only its immutable result comes back to the UI thread, where comparing
//   it with what each catalogue holds is a few hundred lookups.
// - It walks every third beat. A change seen is confirmed by a second walk on the next
//   beat, and read only when the two agree: a burst of saves, or a move that is a
//   delete and a write, is taken once, settled. The delay is at most about four
//   seconds.
// - The changed files are parsed on a worker thread too, from copies; the swap of the
//   catalogue is the only part that runs on the UI thread.
// - Everything this client writes bumps a generation count. A walk or a read that
//   began before a bump may have seen its own write half done, so its result is
//   dropped, as is one that returns after the set changed. (A result that returns
//   after the client closes is posted to a dispatcher that no longer runs.)
//
// A tree that can't be walked (a share that is down, a drive taken out) says nothing
// about what is in it: the catalogues keep what they hold.
public sealed class NavFolderWatcher
{
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly NavFolderManager _folders;
    private readonly LogService? _log;
    private readonly int _pollEvery;
    private readonly Func<string, Task<NavTreeScan>> _walk;
    private readonly Func<Func<Action>, Task<Action>> _read;
    private readonly Action<Action> _post;

    private Task _inFlight = Task.CompletedTask;
    private int _beats;
    private NavTreeScan? _awaitingConfirmation;
    private string? _lastSet;
    private bool _rootSeen;
    private bool _rootWarned;

    public NavFolderWatcher(LoopManager loops, LairManager lairs, NavFolderManager folders, LogService? log = null)
        : this(loops, lairs, folders, log, pollEvery: 3, walk: null, read: null, post: null)
    {
    }

    // The tree walk and the file reads run off the UI thread (a share that is slow or
    // down would freeze the client), and what they found is posted back to the UI
    // thread, where the catalogues live. Each hop is a parameter; the public
    // constructor passes the thread pool and the dispatcher.
    internal NavFolderWatcher(LoopManager loops, LairManager lairs, NavFolderManager folders, LogService? log,
        int pollEvery, Func<string, Task<NavTreeScan>>? walk, Func<Func<Action>, Task<Action>>? read, Action<Action>? post)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(folders);
        _loops = loops;
        _lairs = lairs;
        _folders = folders;
        _log = log;
        _pollEvery = pollEvery;
        _walk = walk ?? (root => Task.Run(() => NavTreeScan.Walk(root, LoopManager.LoopFileSuffix, LairManager.LairFileSuffix)));
        _read = read ?? (job => Task.Run(job));
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    // The walk and read now under way, or the last one; completed when none is.
    internal Task InFlight => _inFlight;

    // One heartbeat. UI thread.
    public void OnHeartbeat()
    {
        if (!_inFlight.IsCompleted) return;
        string? set = _loops.SetName;
        if (set != _lastSet)
        {
            _lastSet = set;
            _awaitingConfirmation = null;
            _rootSeen = false;
            _rootWarned = false;
            _folders.ForgetBaseline();
        }
        if (set is null) return;
        _beats++;
        if (_awaitingConfirmation is null && _beats % _pollEvery != 0) return;

        _inFlight = PollAsync(set, AppPaths.GameDataSetLoopsFolder(set), Generation());
    }

    private long Generation() => _loops.Generation + _lairs.Generation + _folders.Generation;

    private async Task PollAsync(string set, string root, long generation)
    {
        try
        {
            NavTreeScan scan = await _walk(root).ConfigureAwait(false);
            Func<Action>? job = await OnUiThread(() => Judge(scan, set, generation)).ConfigureAwait(false);
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

    // UI thread. What to read now, or null when there is nothing to do yet.
    private Func<Action>? Judge(NavTreeScan scan, string set, long generation)
    {
        if (!Current(set, generation))
        {
            _awaitingConfirmation = null;
            return null;
        }
        if (!scan.Complete)
        {
            _awaitingConfirmation = null;
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
}
