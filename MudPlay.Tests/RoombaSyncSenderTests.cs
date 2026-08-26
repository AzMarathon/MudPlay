using System;
using System.Collections.Generic;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public sealed class RoombaSyncSenderTests
{
    // Manual scheduler: records each (delay, action) the sender schedules and runs
    // them one at a time on demand, so a test can inject a clobber between pumps
    // and inspect the delay each action was scheduled with.
    private sealed class ManualScheduler
    {
        public readonly List<(TimeSpan Delay, Action Action)> Pending = new();
        public void Schedule(TimeSpan delay, Action action) => Pending.Add((delay, action));
        public bool Idle => Pending.Count == 0;
        public TimeSpan RunNext()
        {
            (TimeSpan delay, Action action) = Pending[0];
            Pending.RemoveAt(0);
            action();
            return delay;
        }
    }

    [Fact]
    public void Enqueue_DrainsLinesInOrder_OneAtATime()
    {
        ManualScheduler sched = new();
        RoombaSyncSender sender = new(sched.Schedule);
        List<string> sent = new();

        sender.Enqueue(sent.Add, new[] { "a", "b", "c" });
        Assert.Empty(sent);                          // nothing until the scheduler runs

        sched.RunNext();
        Assert.Equal(new[] { "a" }, sent);           // exactly one line per pump
        sched.RunNext();
        Assert.Equal(new[] { "a", "b" }, sent);
        sched.RunNext();
        Assert.Equal(new[] { "a", "b", "c" }, sent);
        sched.RunNext();                             // drains empty
        Assert.True(sched.Idle);
    }

    [Fact]
    public void Pacing_FirstSendImmediate_RestPaced()
    {
        ManualScheduler sched = new();
        RoombaSyncSender sender = new(sched.Schedule);
        List<string> sent = new();
        sender.Enqueue(sent.Add, new[] { "a", "b" });

        Assert.Equal(TimeSpan.Zero, sched.RunNext());                  // first send is immediate
        Assert.Equal(RoombaSyncSender.PaceInterval, sched.RunNext());  // then paced ~800ms apart
    }

    [Fact]
    public void Clobber_BacksOffThenResendsLastLine()
    {
        ManualScheduler sched = new();
        RoombaSyncSender sender = new(sched.Schedule);
        List<string> sent = new();
        sender.Enqueue(sent.Add, new[] { "a", "b", "c" });

        sched.RunNext();                             // sends "a"
        Assert.Equal(new[] { "a" }, sent);

        sender.NoteClobber();                        // "a" was dropped → requeue + backoff

        sched.RunNext();                             // the queued pump becomes a backoff beat
        Assert.Equal(new[] { "a" }, sent);           // nothing sent on the backoff beat
        Assert.Equal(RoombaSyncSender.ClobberBackoff, sched.RunNext());  // resend fires after the backoff
        Assert.Equal(new[] { "a", "a" }, sent);      // "a" resent

        sched.RunNext();                             // "b"
        sched.RunNext();                             // "c"
        Assert.Equal(new[] { "a", "a", "b", "c" }, sent);
    }

    [Fact]
    public void Clobber_WhileIdle_DoesNothing()
    {
        ManualScheduler sched = new();
        RoombaSyncSender sender = new(sched.Schedule);

        Exception? ex = Record.Exception(() => sender.NoteClobber());

        Assert.Null(ex);
        Assert.True(sched.Idle);
    }
}
