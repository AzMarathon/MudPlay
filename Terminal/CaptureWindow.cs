namespace MudPlay.Terminal;

// Stretches of time a reader of the terminal's transcript may copy from. The screen
// stamps each row with the instant it was written; a row is covered only if that
// instant fell in a stretch when capture was on. Deciding by when a row was written,
// not when it scrolled off or was read, is what keeps a login screen out: it is
// still on screen, unscrolled, after the game has been entered.
//
// Used from the UI thread only, like the scrollback ring it is read against.
public sealed class CaptureWindow
{
    // A character re-entering every few seconds would need thousands of rows of
    // history to outlive this many changes.
    private const int MaxChanges = 64;

    private readonly List<(DateTimeOffset At, bool On)> _changes = new();
    // The state before the oldest remembered change.
    private bool _before;

    public CaptureWindow(bool on = false)
    {
        _before = on;
        IsOn = on;
    }

    public bool IsOn { get; private set; }

    // Switch capture on or off from `at`. A row written at exactly `at` takes the
    // new state.
    public void Set(bool on, DateTimeOffset at)
    {
        if (on == IsOn) return;
        IsOn = on;
        _changes.Add((at, on));
        if (_changes.Count <= MaxChanges) return;
        _before = _changes[0].On;
        _changes.RemoveAt(0);
    }

    // Whether a row written at writeTime may be copied.
    public bool Covers(DateTimeOffset writeTime)
    {
        bool on = _before;
        foreach ((DateTimeOffset at, bool state) in _changes)
        {
            if (at > writeTime) break;
            on = state;
        }
        return on;
    }
}
