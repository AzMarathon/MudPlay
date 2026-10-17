using System.Diagnostics;

namespace MudPlay.Tests;

// A clock that moves only when the test moves it. Delays started on it come due as
// it passes them, in the order they fall due, on the test's own thread; how busy
// the machine is never moves it.
internal sealed class ManualClock
{
    private readonly DateTimeOffset _origin;
    private readonly List<(DateTimeOffset Due, long Order, TaskCompletionSource Done)> _waits = new();
    private long _order;

    public ManualClock() : this(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)) { }

    public ManualClock(DateTimeOffset start)
    {
        _origin = start;
        Now = start;
    }

    public DateTimeOffset Now { get; private set; }

    // The same instant as a Stopwatch timestamp.
    public long Timestamp() => (Now - _origin).Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;

    // How many delays are still to come due.
    public int Waiting => _waits.Count;

    // As Task.Delay: a delay of nothing has nothing to wait for.
    public Task Delay(TimeSpan after)
    {
        if (after <= TimeSpan.Zero) return Task.CompletedTask;
        TaskCompletionSource done = new();
        _waits.Add((Now + after, _order++, done));
        return done.Task;
    }

    // Moves on to the earliest delay and ends it. False when none is waiting.
    public bool RunNext()
    {
        if (_waits.Count == 0) return false;
        int next = 0;
        for (int i = 1; i < _waits.Count; i++)
            if (_waits[i].Due < _waits[next].Due
                || (_waits[i].Due == _waits[next].Due && _waits[i].Order < _waits[next].Order))
                next = i;
        (DateTimeOffset due, _, TaskCompletionSource done) = _waits[next];
        _waits.RemoveAt(next);
        if (due > Now) Now = due;
        done.SetResult();
        return true;
    }

    // Moves the clock on, ending each delay that comes due on the way.
    public void Advance(TimeSpan by)
    {
        DateTimeOffset to = Now + by;
        while (_waits.Count > 0 && _waits.Min(w => w.Due) <= to) RunNext();
        Now = to;
    }
}
