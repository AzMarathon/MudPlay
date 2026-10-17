using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Raises PlayerDied on the local player's death line, "You have been killed!"
// (KnownPatterns.UserKilled). The game prints it for every death by damage, the
// last life's included, and names no killer (GAME_MECHANICS "Death lines & the
// miracle-save").
//
// Its own event beside RoomTracker.PlayerDeathObserved, which DeathDetector raises
// at the lives readout that follows: this line comes first, while the HP the
// character died at is still the last one read (DeathFloorTracer), and it is the
// only sign of a death on the last life, which prints no lives readout. A suicide
// prints neither this line nor a death by damage's readout. Each consumer takes
// one of the two events, never both, so no death is acted on twice.
public sealed class DeathLineWatcher : IDisposable
{
    // LogService category for the death notification row.
    public const string LogCategory = "Death";

    private readonly LogService? _log;
    private readonly IDisposable _killedSub;
    private bool _disposed;

    // Fired once per death line. Consumers run on the MessageRouter's marshalled
    // thread.
    public event Action? PlayerDied;

    public DeathLineWatcher(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _killedSub = router.Subscribe(KnownPatterns.UserKilled, OnKilled);
    }

    private void OnKilled(MatchResult _)
    {
        _log?.Warn(LogCategory, "You have been killed!");
        PlayerDied?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _killedSub.Dispose();
    }
}
