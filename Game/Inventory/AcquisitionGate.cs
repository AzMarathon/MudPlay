using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Shared driver for MovementCoordinator.AcquisitionGate. Both the item engine
// (AutoGetItemsManager) and the cash engine (CashManager) feed this one
// instance; it owns the single assert/clear of the gate so the two engines'
// states are AND-ed — the walker resumes only once both have finished looting.
//
// MovementCoordinator keys gates by name in a HashSet, so a gate is
// single-owner: if both engines asserted "Acquisition" directly, the first to
// clear would drop it while the other still wanted it held. This driver is the
// single owner; engines report activity and it ANDs.
//
// The gate is held while any condition is live:
//   - Pending deferred items: AutoGetItemsManager queued gets that wait for
//     the room's combat to finish. Held with no timeout until flushed or the
//     room changes. Asserting here — while the Combat gate is still up, on the
//     same EntitiesObserved pass that CombatStateTracker later uses to clear
//     the Combat gate — is what defeats the synchronous walker-resume race:
//     without it the walker would step out the instant Combat clears, before
//     the loot flush runs.
//   - Outstanding gets: get commands dispatched but not yet confirmed. The
//     server DOES echo a per-get confirmation — `You took <item>.` for an item,
//     `You picked up <N> <coin>.` for coin (N=0 meaning "full, took nothing" —
//     still a resolution) — so the engines call NoteGetConfirmed as each lands
//     and the gate releases the instant the last one is accounted for. Movement
//     resumes as soon as looting is actually done, not on a fixed timer.
//   - Settle window: a fallback ceiling on the wait. A get that never confirms
//     (a failure line we don't yet parse) would otherwise strand the hold, so
//     once gets stop flowing for SettleWindow the gate releases regardless.
public sealed class AcquisitionGate : IDisposable
{
    // Asserter identity recorded in the [Gate] log + MovementCoordinator.History.
    public const string AsserterName = "Acquisition";

    // Fallback ceiling on the post-get hold. Normally the gate releases the
    // moment the last get is confirmed (NoteGetConfirmed); this timer only fires
    // when a get never confirms — a failure line we don't yet parse — so it can't
    // strand the walker. Re-armed on each get; released this long after the last
    // one with no confirmation. Kept short so even the fallback resumes promptly.
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(400);

    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly DispatcherTimer _settle;

    private int _pendingDeferred;
    private int _outstandingGets;   // dispatched but not yet confirmed
    private bool _asserted;
    private bool _disposed;

    public AcquisitionGate(MovementCoordinator coordinator, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _log = log;
        _settle = new DispatcherTimer { Interval = SettleWindow };
        _settle.Tick += OnSettleElapsed;
    }

    // Note that the item engine queued count gets to fire once the room's
    // combat finishes. Asserts the gate now — while the Combat gate is still
    // held — so the walker can't step out the instant combat clears, before the
    // deferred flush runs. A count of 0 is a no-op (nothing to wait for).
    public void NoteDeferredPending(int count)
    {
        _pendingDeferred = count;
        if (count > 0) Assert($"deferred-pending={count}");
    }

    // Note a get command was just dispatched (item or cash). Asserts the gate,
    // counts the get as outstanding, and re-arms the fallback settle window. The
    // gate normally releases on the matching NoteGetConfirmed; the settle only
    // catches a get that never confirms.
    public void NoteGetSent()
    {
        _outstandingGets++;
        Assert("get-sent");
        _settle.Stop();
        _settle.Start();
    }

    // Note the server confirmed a get landed — `You took <item>.` (item) or
    // `You picked up <N> <coin>.` (cash, N=0 = full/took-nothing but still a
    // resolution). Drains one outstanding get; once none remain and no deferred
    // items are pending, the gate releases immediately (the fluid path — no wait
    // on the settle timer). A stray confirmation with nothing outstanding (e.g. a
    // manual get) is ignored.
    public void NoteGetConfirmed()
    {
        if (_outstandingGets == 0) return;
        _outstandingGets--;
        if (_outstandingGets == 0 && _pendingDeferred == 0)
        {
            _settle.Stop();
            Release("gets-confirmed");
        }
    }

    // The deferred queue was flushed or discarded — no items wait on combat
    // anymore. If the flush armed the settle window (gets went out) the gate
    // stays held until that elapses; if the queue was dropped without any gets
    // (e.g. the user walked away mid-combat), the gate releases immediately.
    public void NoteDeferredCleared()
    {
        _pendingDeferred = 0;
        if (!_settle.IsEnabled) Release("deferred-cleared");
    }

    private void OnSettleElapsed(object? sender, EventArgs e)
    {
        _settle.Stop();
        if (_pendingDeferred > 0) return;   // still waiting on combat — keep held
        // Fallback: gets that never confirmed are presumed done — drop them.
        _outstandingGets = 0;
        Release("settle-elapsed");
    }

    private void Assert(string reason)
    {
        if (_asserted) return;
        _asserted = true;
        _coordinator.AssertGate(MovementCoordinator.AcquisitionGate, AsserterName, reason);
    }

    private void Release(string reason)
    {
        if (!_asserted) return;
        _asserted = false;
        _outstandingGets = 0;   // clean slate for the next collection cycle
        _coordinator.ClearGate(MovementCoordinator.AcquisitionGate, AsserterName, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settle.Stop();
        _settle.Tick -= OnSettleElapsed;
        Release("disposed");
    }
}
