namespace MudPlay.Game.Calculators;

// Turns a stream of time-to-level estimates into a clock that counts down. The
// estimate is recomputed constantly (every repaint, every kill) from a windowed
// exp/hour that drifts, so shown raw it jumps around; a player wants a timer.
// Once anchored, the countdown just runs, and a fresh estimate only re-anchors it
// when it disagrees by more than the tolerance — a real change (a much better or
// worse stretch, a level gained or trained, a pause long enough to drag the rate
// down), not the kill-to-kill wobble. A countdown that reaches zero before the
// estimate does re-anchors too, rather than sitting at zero claiming "ready".
public sealed class TnlCountdown
{
    private static readonly TimeSpan MinTolerance = TimeSpan.FromSeconds(30);
    private const double ToleranceFraction = 0.15;

    private DateTimeOffset? _deadline;

    public TimeSpan? Remaining(TimeSpan? estimate, DateTimeOffset now)
    {
        if (estimate is not { } est)
        {
            _deadline = null;
            return null;
        }
        if (est <= TimeSpan.Zero)
        {
            _deadline = null;
            return TimeSpan.Zero;
        }

        if (_deadline is { } deadline)
        {
            TimeSpan running = deadline - now;
            TimeSpan tolerance = TimeSpan.FromTicks(Math.Max(MinTolerance.Ticks, (long)(running.Ticks * ToleranceFraction)));
            if (running > TimeSpan.Zero && (est - running).Duration() <= tolerance) return running;
        }
        _deadline = now + est;
        return est;
    }
}
