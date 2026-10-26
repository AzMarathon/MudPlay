using System;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A compacting collection stops the client for tens of milliseconds, so it may run
// only where no one can feel it: long after startup, after minutes with no fight,
// walk, loop, Auto-Lair or errand, on a quiet wire, and seldom; or once as the window
// is minimized when nothing is fighting or moving. A slow one makes the next wait
// longer.
public sealed class IdleCompactionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Slow = IdleCompactionPolicy.PauseBound + TimeSpan.FromMilliseconds(1);

    private readonly IdleCompactionPolicy _policy = new(Start);
    private DateTimeOffset _now = Start;
    private long _lines;

    // Half a minute passes; the client is busy or not, and lines arrive or not.
    private bool Sample(bool busy = false, int newLines = 0)
    {
        _now += TimeSpan.FromSeconds(30);
        _lines += newLines;
        return _policy.Sample(_now, busy, _lines);
    }

    // Samples until one says a compaction is due, or the time runs out.
    private TimeSpan UntilDue(TimeSpan within)
    {
        DateTimeOffset from = _now;
        while (_now - from < within)
            if (Sample()) return _now - from;
        return TimeSpan.MaxValue;
    }

    [Fact]
    public void NeverInTheFirstHalfHour_ThenOnceTheClientHasBeenIdleFiveMinutes()
    {
        while (_now - Start < IdleCompactionPolicy.MinUptime - TimeSpan.FromSeconds(30))
            Assert.False(Sample());
        Assert.True(Sample());
    }

    [Fact]
    public void AFightOrAWalk_PutsTheStableTimeBackToTheStart()
    {
        _now += IdleCompactionPolicy.MinUptime;
        Sample();
        _now += TimeSpan.FromMinutes(4);
        Assert.False(Sample(busy: true));
        // Five quiet minutes from the end of the fight, not from before it.
        for (int i = 0; i < 10; i++) Assert.False(Sample());
        Assert.True(Sample());
    }

    [Fact]
    public void ALineFromTheGame_HoldsItForTheNextQuietSample()
    {
        _now += IdleCompactionPolicy.MinUptime;
        for (int i = 0; i < 10; i++) Sample();
        Assert.False(Sample(newLines: 3));   // five stable minutes, but a line came
        Assert.True(Sample());
    }

    [Fact]
    public void AtMostOncePerInterval_AndASlowPauseDoublesIt()
    {
        _now += IdleCompactionPolicy.MinUptime;
        Assert.True(UntilDue(TimeSpan.FromHours(1)) < TimeSpan.MaxValue);
        _policy.Compacted(_now, Fast);
        Assert.Equal(IdleCompactionPolicy.BaseInterval, UntilDue(TimeSpan.FromHours(1)));

        _policy.Compacted(_now, Slow);
        Assert.Equal(IdleCompactionPolicy.BaseInterval * 2, _policy.Interval);
        Assert.Equal(IdleCompactionPolicy.BaseInterval * 2, UntilDue(TimeSpan.FromHours(2)));
        _policy.Compacted(_now, Slow);
        Assert.Equal(IdleCompactionPolicy.BaseInterval * 4, _policy.Interval);
        // A fast one puts the interval back.
        _policy.Compacted(_now, Fast);
        Assert.Equal(IdleCompactionPolicy.BaseInterval, _policy.Interval);
    }

    [Fact]
    public void TheIntervalStopsGrowingAtItsCeiling()
    {
        for (int i = 0; i < 20; i++) _policy.Compacted(_now, Slow);
        Assert.Equal(IdleCompactionPolicy.MaxInterval, _policy.Interval);
    }

    [Fact]
    public void Minimized_RunsAtOnceWhenNothingIsBusy_NotWhenFightingOrMoving_AndNotTwiceInARow()
    {
        Assert.False(_policy.Minimized(_now, busy: true));
        Assert.True(_policy.Minimized(_now, busy: false));
        _policy.Compacted(_now, Fast);
        Assert.False(_policy.Minimized(_now + TimeSpan.FromSeconds(30), busy: false));
        Assert.True(_policy.Minimized(_now + IdleCompactionPolicy.MinimizedSpacing, busy: false));

        // After a slow pause, a minimize waits out the longer interval too.
        _policy.Compacted(_now, Slow);
        Assert.False(_policy.Minimized(_now + IdleCompactionPolicy.MinimizedSpacing, busy: false));
        Assert.True(_policy.Minimized(_now + _policy.Interval, busy: false));
    }
}
