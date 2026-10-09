using System.Text;

namespace MudPlay.Services;

// What the player meant by a board log-off command they sent themselves: typed,
// from a macro or alias, or relayed by a remote `@do`. Players use `;o` to drop the
// line and come straight back, and `=x` to drop it and stay off (user, 2026-10-09),
// so the disconnect that follows one of them is classified by which it was instead
// of by the per-BBS reconnect toggles.
//
// The command only counts for a short while. A board that doesn't know it answers
// with something else and stays connected, and a drop minutes later is not its doing.
public sealed class SentExitCommand
{
    public enum Intent { None, Relog, StayDown }

    public const string RelogCommand = ";o";
    public const string StayDownCommand = "=x";

    // The board answers a log-off at once; this only has to outlast a slow link.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly Func<DateTimeOffset> _now;
    private Intent _intent;
    private DateTimeOffset _sentAt;

    public SentExitCommand(Func<DateTimeOffset>? now = null)
        => _now = now ?? (() => DateTimeOffset.UtcNow);

    // One outbound line. Anything other than the two commands leaves an earlier one
    // standing: the board drops the line on its own schedule, and a queued command
    // behind the log-off doesn't cancel it.
    public void ObserveOutbound(byte[] data)
    {
        if (data.Length is 0 or > 16) return;
        string line = Encoding.Latin1.GetString(data).Trim('\r', '\n', ' ', '\0');
        Intent intent = Classify(line);
        if (intent == Intent.None) return;
        _intent = intent;
        _sentAt = _now();
    }

    public static Intent Classify(string line) =>
        line.Equals(RelogCommand, StringComparison.OrdinalIgnoreCase) ? Intent.Relog
        : line.Equals(StayDownCommand, StringComparison.OrdinalIgnoreCase) ? Intent.StayDown
        : Intent.None;

    // Read and clear, at the disconnect. None when no log-off command went out, or
    // the one that did is too old to have caused this drop.
    public Intent Consume()
    {
        Intent intent = _intent;
        _intent = Intent.None;
        return _now() - _sentAt <= Window ? intent : Intent.None;
    }
}
