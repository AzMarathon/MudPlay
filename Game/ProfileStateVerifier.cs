using MudPlay.Services;

namespace MudPlay.Game;

// Reads a copied profile's character off the game on its first entry. A copy carries
// another character's settings (CharacterProfile.StateUnverified), so nothing the
// client knows about this one is trusted until a full `stat` screen and an inventory
// list have landed. A login normally sends both itself; this covers an entry made by
// hand, and keeps the mark until both are in.
public sealed class ProfileStateVerifier
{
    public const string LogCategory = "ProfileVerify";

    // Leaves room for the login's own `stat` / `i` to land before asking again.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _pending;
    private readonly Action _markVerified;
    private readonly Func<bool> _canAskNow;
    private readonly Func<bool> _inventoryLoaded;
    private readonly Action<string> _send;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private bool _statSeen;
    private DateTimeOffset? _settleFrom;
    private DateTimeOffset _lastAsked = DateTimeOffset.MinValue;

    public ProfileStateVerifier(
        Func<bool> pending, Action markVerified, Func<bool> canAskNow,
        Func<bool> inventoryLoaded, Action<string> send,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        _pending = pending;
        _markVerified = markVerified;
        _canAskNow = canAskNow;
        _inventoryLoaded = inventoryLoaded;
        _send = send;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Another profile loaded, or the connection dropped: what this session saw no
    // longer counts.
    public void Reset()
    {
        _statSeen = false;
        _settleFrom = null;
        _lastAsked = DateTimeOffset.MinValue;
    }

    // A `stat` screen showed Hits and the pool.
    public void OnStatScreen()
    {
        if (!_pending() || _statSeen) return;
        _statSeen = true;
        _settleFrom = _now();
        // The inventory ask is a new question, not a retry of the `stat`.
        _lastAsked = DateTimeOffset.MinValue;
    }

    // Heartbeat.
    public void Poll()
    {
        if (!_pending() || !_canAskNow()) return;
        DateTimeOffset now = _now();
        _settleFrom ??= now;
        if (now - _settleFrom < Settle) return;

        if (_statSeen && _inventoryLoaded())
        {
            _markVerified();
            _log?.Info(LogCategory, "copied profile: stats and inventory read — state verified");
            return;
        }
        if (now - _lastAsked < Retry) return;
        _lastAsked = now;
        string command = _statSeen ? "i" : "stat";
        _log?.Info(LogCategory, $"copied profile: this character's state hasn't been read — sending `{command}`");
        _send(command);
    }
}
