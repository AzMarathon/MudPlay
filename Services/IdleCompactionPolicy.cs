namespace MudPlay.Services;

// When MemoryMaintenance may run a full compacting collection: a stop-the-world pause
// of tens of milliseconds that hands the heap's free and fragmented space (the large-
// object heap's holes included) back to the system. Taken only where no one can feel
// it:
//
//  - Idle: the client has been up a while (MinUptime), nothing has fought, moved or
//    run an errand for StableFor, no more than QuietLines game lines arrived since the
//    last sample (a cast burst or a fight brings more), and the last compaction is
//    Interval old.
//  - Minimized: once as the main window goes down, when nothing is fighting or
//    moving, and not again within MinimizedSpacing.
//
// A pause over PauseBound doubles Interval (to at most MaxInterval), so a client whose
// heap compacts slowly is compacted seldom; one under it puts Interval back. The
// minimize trigger keeps to a doubled Interval too. Not thread-safe: MemoryMaintenance
// holds its lock around every call.
public sealed class IdleCompactionPolicy
{
    public static readonly TimeSpan MinUptime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StableFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(4);
    public static readonly TimeSpan MinimizedSpacing = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan PauseBound = TimeSpan.FromMilliseconds(150);
    // Game lines a sample may bring and still count as quiet: a connected client on a
    // busy board hears gossip and people passing all the time, while a fight or a cast
    // brings more (and a fight is busy anyway).
    public const int QuietLines = 10;

    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _stableSince;
    private long _lines = -1;
    private DateTimeOffset? _last;

    public IdleCompactionPolicy(DateTimeOffset startedAt) => _startedAt = startedAt;

    // The shortest time between two idle compactions now.
    public TimeSpan Interval { get; private set; } = BaseInterval;

    // A sample of the client, taken every half minute or so: busy when anything is
    // fighting, moving or running an errand; lines is the running count of game lines
    // received. True when an idle compaction is due now.
    public bool Sample(DateTimeOffset now, bool busy, long lines)
    {
        bool quiet = _lines >= 0 && lines - _lines <= QuietLines;
        _lines = lines;
        if (busy)
        {
            _stableSince = null;
            return false;
        }
        _stableSince ??= now;
        return quiet
            && now - _startedAt >= MinUptime
            && now - _stableSince >= StableFor
            && (_last is not { } last || now - last >= Interval);
    }

    // The main window was minimized: true when a compaction may run for it now.
    public bool Minimized(DateTimeOffset now, bool busy)
    {
        if (busy) return false;
        TimeSpan spacing = Interval > BaseInterval ? Interval : MinimizedSpacing;
        return _last is not { } last || now - last >= spacing;
    }

    // A compaction ran at now and paused the client for pause.
    public void Compacted(DateTimeOffset now, TimeSpan pause)
    {
        _last = now;
        Interval = pause > PauseBound
            ? TimeSpan.FromTicks(Math.Min(Interval.Ticks * 2, MaxInterval.Ticks))
            : BaseInterval;
    }
}
