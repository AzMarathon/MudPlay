using System.Threading.Channels;

namespace MudPlay.Services;

// The hand-off from the threads that log to the one background task that writes
// a log file (RecentLogFile, ProgramLogFile). Posting never waits: the queue
// holds `depth` items, and while the writer is stuck (a stalled disk, a network
// folder gone quiet) the oldest waiting make room for the newest and are counted,
// so the file can say how many went. What is kept is the end of the log, the
// part a hang or crash report needs.
//
// A barrier posted behind the items is released once the writer has finished the
// batch it was read in (EndBatch): that is how a caller learns that what it
// logged has reached the disk. A barrier the full queue lets go of is released
// too, so nothing waits on one for ever.
internal sealed class LogEntryQueue<T>
{
    private readonly Channel<Slot> _channel;

    // Barriers read in the current batch. Touched only by the writer.
    private readonly List<Action> _batchBarriers = new();

    // Items the full queue let go of since the writer last took the count.
    // Counted on the threads that log, taken by the writer.
    private int _dropped;

    // Which items count as entries when let go of; null counts every item.
    private readonly Func<T, bool>? _isEntry;

    // An empty slot (no value, no barrier) only wakes the writer.
    private readonly record struct Slot(T Value, bool HasValue, Action? Barrier);

    public LogEntryQueue(int depth, Func<T, bool>? isEntry = null)
    {
        _isEntry = isEntry;
        _channel = Channel.CreateBounded<Slot>(
            new BoundedChannelOptions(depth) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            OnDropped);
    }

    // On the producer's thread: never waits. False once the queue is completed.
    public bool Post(T value) => _channel.Writer.TryWrite(new Slot(value, true, null));

    // Wake the writer with nothing to write, so it acts on a switch now rather
    // than at the next entry.
    public void Wake() => _channel.Writer.TryWrite(default);

    private void OnDropped(Slot slot)
    {
        if (slot.HasValue && (_isEntry?.Invoke(slot.Value) ?? true)) Interlocked.Increment(ref _dropped);
        slot.Barrier?.Invoke();
    }

    // Completes once everything posted before the call has been written (or let
    // go of by the full queue).
    public Task WhenWrittenAsync()
    {
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Slot(default!, false, () => barrier.TrySetResult()))) barrier.TrySetResult();
        return barrier.Task;
    }

    // WhenWrittenAsync for a caller that cannot await (the crash handler, process
    // exit). True when it all landed within `bound`; false when the writer is
    // still at it.
    public bool WaitWritten(TimeSpan bound)
    {
        ManualResetEventSlim written = new();
        if (!_channel.Writer.TryWrite(new Slot(default!, false, written.Set))) return true;
        if (!written.Wait(bound)) return false;   // left undisposed: the writer may still set it
        written.Dispose();
        return true;
    }

    public ValueTask<bool> WaitToReadAsync() => _channel.Reader.WaitToReadAsync();

    // The next item of the batch. Barriers met on the way are held for EndBatch.
    public bool TryRead(out T value)
    {
        while (_channel.Reader.TryRead(out Slot slot))
        {
            if (slot.Barrier is { } barrier) _batchBarriers.Add(barrier);
            else if (slot.HasValue)
            {
                value = slot.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    // The batch is written and flushed (or given up on): release its barriers.
    public void EndBatch()
    {
        foreach (Action barrier in _batchBarriers) barrier();
        _batchBarriers.Clear();
    }

    // How many items the full queue let go of since the last call.
    public int TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    // No more posts; the writer's WaitToReadAsync ends once the queue is empty.
    // False when it was completed already.
    public bool Complete() => _channel.Writer.TryComplete();
}
