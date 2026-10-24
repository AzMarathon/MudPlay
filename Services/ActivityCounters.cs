namespace MudPlay.Services;

// Running counts of the work that grows with play: lines received from the
// game, terminal renders and map redraws. The memory log turns them into rates
// per sample, so a native climb can be set beside what the client was doing
// while it climbed; the bug and hang reports give the session's totals. Counted
// always: an interlocked add is cheaper than asking whether to count.
public sealed class ActivityCounters
{
    private long _lines, _renders, _mapRedraws;

    public readonly record struct Counts(long Lines, long Renders, long MapRedraws)
    {
        public Counts Since(Counts then) => new(Lines - then.Lines, Renders - then.Renders, MapRedraws - then.MapRedraws);
    }

    public void LineReceived() => Interlocked.Increment(ref _lines);
    public void TerminalRendered() => Interlocked.Increment(ref _renders);
    public void MapRedrawn() => Interlocked.Increment(ref _mapRedraws);

    // Any thread.
    public Counts Read() => new(Interlocked.Read(ref _lines), Interlocked.Read(ref _renders), Interlocked.Read(ref _mapRedraws));
}
