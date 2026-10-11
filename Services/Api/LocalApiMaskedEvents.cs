using System.Threading.Channels;

namespace MudPlay.Services.Api;

// Masks the saved login name and passwords in what GET /events streams, as a bug
// report and GET /log do: the stream is the same log leaving the client live.
//
// An event is made wherever it happens (the Telnet read loop, the UI thread), and
// neither may wait while the saved values are read and the masks are built. So a
// producer only queues here, and one pump masks and hands on to the fan-out.
//
// The scrubber holds the values in clear, so none is kept: one is built for each
// run of queued events (what gathers in DefaultGather) and dropped with it. If it can't be built, that run is
// not sent, and the stream says how many events it lost and why.
public sealed class LocalApiMaskedEvents : IDisposable
{
    // Events waiting to be masked. Far more than the pump is ever behind by; past
    // it a producer's event is counted as lost rather than held.
    public const int QueueDepth = 4096;

    // The stream's own event for what it could not send.
    public const string LostEvent = "lost";

    // How long the pump lets events gather after the first of a run before it
    // builds the masks for them. The build reads the saved values on the UI
    // thread (a millisecond or two of it), and a steady log would otherwise ask
    // for one with every entry: this way it is one a run, at most seven a
    // second. Short enough that the stream still reads as live.
    public static readonly TimeSpan DefaultGather = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _gather;

    private readonly record struct Pending(string Name, Func<BugReportScrub.Scrubber, object> Payload);

    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(QueueDepth)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: refuse rather than block
        });
    private readonly Func<Task<BugReportScrub.Scrubber>> _buildScrubber;
    private readonly Func<bool> _anyoneListening;
    private readonly Action<string, object> _send;
    private readonly CancellationTokenSource _cts = new();
    // Taken once: the pump must not ask a disposed source for it.
    private readonly CancellationToken _stopped;
    private int _refused;

    // buildScrubber: the masks as the saved values stand now. anyoneListening:
    // false while no stream is open, when nothing is queued at all. send: the
    // fan-out's Send. gather: see DefaultGather.
    public LocalApiMaskedEvents(
        Func<Task<BugReportScrub.Scrubber>> buildScrubber, Func<bool> anyoneListening, Action<string, object> send,
        TimeSpan? gather = null)
    {
        ArgumentNullException.ThrowIfNull(buildScrubber);
        ArgumentNullException.ThrowIfNull(anyoneListening);
        ArgumentNullException.ThrowIfNull(send);
        _buildScrubber = buildScrubber;
        _anyoneListening = anyoneListening;
        _send = send;
        _gather = gather ?? DefaultGather;
        _stopped = _cts.Token;
        _ = Task.Run(PumpAsync);
    }

    // Queue one event. payload makes the event's body from the scrubber, on the
    // pump: every string in it that could carry text from the game, the user or
    // the log goes through the scrubber there. Never blocks.
    public void Post(string eventName, Func<BugReportScrub.Scrubber, object> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_anyoneListening()) return;
        if (!_queue.Writer.TryWrite(new Pending(eventName, payload))) Interlocked.Increment(ref _refused);
    }

    private async Task PumpAsync()
    {
        ChannelReader<Pending> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopped).ConfigureAwait(false))
            {
                // The run gathers in the queue, in order, while this waits.
                if (_gather > TimeSpan.Zero) await Task.Delay(_gather, _stopped).ConfigureAwait(false);
                BugReportScrub.Scrubber? scrubber = null;
                string? unmaskable = null;
                try { scrubber = await _buildScrubber().ConfigureAwait(false); }
                catch (Exception ex) { unmaskable = ex.GetType().Name; }

                int unsent = 0;
                while (reader.TryRead(out Pending pending))
                {
                    if (scrubber is null) { unsent++; continue; }
                    // One event that can't be made must not end the stream for
                    // the rest.
                    try { _send(pending.Name, pending.Payload(scrubber)); }
                    catch (Exception) { unsent++; }
                }

                if (unmaskable is not null)
                    _send(LostEvent, new
                    {
                        count = unsent,
                        reason = $"the saved account details could not be read, so these events could not be masked ({unmaskable})",
                    });
                else if (unsent > 0)
                    _send(LostEvent, new { count = unsent, reason = "these events could not be written" });

                if (Interlocked.Exchange(ref _refused, 0) is > 0 and int refused)
                    _send(LostEvent, new { count = refused, reason = $"more than {QueueDepth} events were waiting to be masked" });
            }
        }
        catch (OperationCanceledException) { /* Dispose stopped us */ }
    }

    public void Dispose()
    {
        if (!_queue.Writer.TryComplete()) return;   // disposed already
        _cts.Cancel();
        _cts.Dispose();
    }
}
