using System.Threading;

namespace MudPlay.Tests;

// Where code started under it goes on after each of its awaits. Left to the thread
// pool, how soon it went on was up to how busy the machine was; here each await
// posts its continuation to this context, and the test runs them on its own
// thread, in order, when it calls Drain.
internal sealed class ContinuationPump : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_posted) _posted.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("the code under test only posts");

    public override SynchronizationContext CreateCopy() => this;

    // Starts the code under this context, so its first await is posted here.
    public T Run<T>(Func<T> start)
    {
        SynchronizationContext? prior = Current;
        SetSynchronizationContext(this);
        try { return start(); }
        finally { SetSynchronizationContext(prior); }
    }

    public void Run(Action start) => Run(() => { start(); return 0; });

    // Everything posted, and everything that posts in turn, until nothing is left.
    // Returns how many continuations ran.
    public int Drain()
    {
        int ran = 0;
        SynchronizationContext? prior = Current;
        SetSynchronizationContext(this);
        try
        {
            while (TryTake(out (SendOrPostCallback Callback, object? State) next))
            {
                next.Callback(next.State);
                ran++;
            }
        }
        finally { SetSynchronizationContext(prior); }
        return ran;
    }

    private bool TryTake(out (SendOrPostCallback Callback, object? State) next)
    {
        lock (_posted) return _posted.TryDequeue(out next);
    }
}
