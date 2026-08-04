using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// A disconnect can strand the shared Acquisition movement gate: cash/items deferred
// mid-fight (CollectAfterCombatFinished) hold the gate via NoteDeferredPending, which
// has no self-heal, and the reconnect re-display doesn't reliably flush it (an empty
// room carries no "Also here:" line for the classifier, and a re-display arriving while
// combat is still asserted just re-defers). So the loop sits Paused after reconnect
// until the user types `rm` — the missing post-combat re-render that runs the flush.
//
// This releases that stranded hold on the first in-game prompt after a reconnect. The
// prompt (not the room display) is the fire signal so it lands even in a dark room,
// bounding the fire to the reconnect window — same rationale as PartyRejoinCoordinator.
// By then the connection is live and the room re-displayed, so dropping the hold
// resumes the loop from the correct confirmed room (the walker was Paused, never mid-
// move, so its position never drifted). Any cash/items genuinely still on the ground
// are re-collected off the reconnect re-display through the normal path — this only
// drops the STALE deferred amounts that were pinning the gate.
//
// Arm() is called on every reconnect; the release is a no-op unless a hold is actually
// stranded, so a clean reconnect with nothing deferred does nothing.
public sealed class DeferredCollectReconnectReleaser : IDisposable
{
    public const string LogCategory = "Reconnect";

    private readonly WirePromptScanner _scanner;
    private readonly Action _releaseDeferred;
    private readonly LogService? _log;
    private bool _armed;
    private bool _disposed;

    public DeferredCollectReconnectReleaser(
        WirePromptScanner scanner, Action releaseDeferred, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(releaseDeferred);
        _scanner = scanner;
        _releaseDeferred = releaseDeferred;
        _log = log;
        _scanner.PromptObserved += OnInGamePrompt;
    }

    // Open the one-shot latch. Called on every reconnect (client.Connected after a
    // prior in-session drop).
    public void Arm() => _armed = true;

    private void OnInGamePrompt(PromptObservation _)
    {
        if (!_armed) return;
        _armed = false;   // one-shot per reconnect
        _log?.Debug(LogCategory, "first in-game prompt after reconnect — releasing any stranded deferred-collect hold");
        _releaseDeferred();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnInGamePrompt;
    }
}
