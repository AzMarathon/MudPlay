using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Reads the follow mode off what the game prints: the `Follow Mode:` row of a `pro`
// sheet and the two replies to `set follow` (GAME_MECHANICS "Following the leader:
// follow / drag movement"). Passive: it never asks for the sheet.
//
// The mode matters to a follower's map. A dragged follower types no move, so the
// room display after each follow line is the only thing that confirms where the
// drag landed; in Blind mode that display never comes and the tracker can't hold
// its place. Few players know the setting exists, so when it turns up as Blind the
// user is told once, with the command that puts it back.
public sealed class FollowModeTracker : IDisposable
{
    private const string LogCategory = "FollowMode";

    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();
    private bool _blindNoticed;
    private bool _disposed;

    public FollowMode Mode { get; private set; } = FollowMode.Unknown;

    // Blind mode was just learned of. Raised once for each time the mode is found
    // Blind after being unknown or Normal, not for every sheet that repeats it.
    public event Action? BlindNoticed;

    public FollowModeTracker(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _subs.Add(router.Subscribe(KnownPatterns.FollowModeRow, OnProfileRow));
        _subs.Add(router.Subscribe(KnownPatterns.FollowModeSetBlind, _ => Note(FollowMode.Blind)));
        _subs.Add(router.Subscribe(KnownPatterns.FollowModeSetNormal, _ => Note(FollowMode.Normal)));
    }

    // Another character, or a new session of this one: what was read no longer holds.
    public void Reset()
    {
        Mode = FollowMode.Unknown;
        _blindNoticed = false;
    }

    private void OnProfileRow(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        Note(match.Groups[0].Equals("Blind", StringComparison.OrdinalIgnoreCase)
            ? FollowMode.Blind
            : FollowMode.Normal);
    }

    private void Note(FollowMode mode)
    {
        if (_disposed) return;
        Mode = mode;
        if (mode != FollowMode.Blind)
        {
            _blindNoticed = false;
            return;
        }
        if (_blindNoticed) return;
        _blindNoticed = true;
        _log?.Warn(LogCategory,
            "Follow mode is Blind: following a party leader prints no room, so the map can't "
            + "track this character while it follows. `set follow normal` puts the room displays back.");
        BlindNoticed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }
}
