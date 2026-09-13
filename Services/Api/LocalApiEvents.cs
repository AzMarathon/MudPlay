using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MudPlay.Services.Api;

// Server-Sent Events fan-out for the local API. One subscriber per open
// GET /events response; each gets every event until its socket dies.
//
// SSE rather than WebSockets because the traffic is one-directional (the client
// only listens) and SSE is plain chunked HTTP — no framing library, no extra
// dependency, and `curl -N` can read it directly, which matters when the whole
// point is diagnosing a wedged client by hand.
//
// THE PRODUCER THREAD IS NEVER TOUCHED BY A SOCKET. Events originate wherever
// they happen — LogService fires on the producer's thread (the Telnet read loop,
// or the UI thread via MessageRouter), gates fire on the UI thread — so a write
// that blocked would block THEM. A socket write blocks as soon as the reader
// stops draining (a suspended process, `curl | head`, a stalled tunnel), and the
// kernel buffer fills in kilobytes; with a direct write, a single stalled reader
// froze the whole app, and asserting a movement gate performed a blocking socket
// write on the render thread. So each subscriber owns a bounded queue and a pump
// task: producers serialise once, enqueue, and return. The pump is also the
// SINGLE writer to its stream, which is what keeps concurrent producers from
// interleaving bytes mid-frame and corrupting the chunked framing.
//
// A reader that falls QueueDepth frames behind is retired rather than buffered
// indefinitely — unbounded would trade a freeze for a leak, and a reader that far
// behind is broken. The drop is logged, because silently losing events is the one
// thing a diagnostic stream must not do.
public sealed class LocalApiEvents : IDisposable
{
    // Comment line every this often. Keeps intermediaries from timing the
    // connection out, and lets a reader notice a dead server during a long quiet
    // stretch — which is exactly the situation being diagnosed.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    // Frames a subscriber may fall behind before it's retired. Generous next to a
    // reader keeping up at all; small next to the memory a wedged one would pin.
    private const int QueueDepth = 1024;

    // Concurrent /events streams. Each costs a pump task and up to QueueDepth
    // buffered frames, and they all hold a valid token — but a token-holding
    // script in a retry loop shouldn't be able to grow either without bound.
    private const int MaxSubscribers = 16;

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly LogService? _log;
    private readonly Timer _heartbeat;
    private volatile bool _disposed;

    public LocalApiEvents(LogService? log = null)
    {
        _log = log;
        _heartbeat = new Timer(_ => Broadcast(":hb\n\n"), null, Heartbeat, Heartbeat);
    }

    public int SubscriberCount => _subscribers.Count;

    // Whether a subscription is still live. A write that finds the socket dead
    // retires it, so the request handler parked on this stream polls here to know
    // when to unwind.
    public bool Contains(Guid id) => _subscribers.ContainsKey(id);

    // One open SSE response plus the queue feeding it. The pump task is the only
    // thing that ever writes to Stream.
    private sealed class Subscriber(HttpListenerResponse response, Stream stream)
    {
        public HttpListenerResponse Response { get; } = response;
        public Stream Stream { get; } = stream;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(QueueDepth)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: refuse rather than block
            });
        public CancellationTokenSource Cts { get; } = new();
    }

    // Adopt a response as an SSE stream. Returns the id so the request loop can
    // retire it when the handler unwinds, or Guid.Empty when we're full — the
    // caller turns that into a 503 rather than a silent no-event connection.
    public Guid Add(HttpListenerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (_disposed || _subscribers.Count >= MaxSubscribers)
        {
            _log?.Warn(LocalApiServer.LogCategory,
                $"refused an event subscriber — already at the {MaxSubscribers} stream limit.");
            return Guid.Empty;
        }

        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        // No Content-Length: the body is open-ended, so the response must be
        // chunked. SendChunked also stops HttpListener buffering our writes.
        response.SendChunked = true;

        Guid id = Guid.NewGuid();
        Subscriber sub = new(response, response.OutputStream);
        _subscribers[id] = sub;
        _ = Task.Run(() => PumpAsync(id, sub), CancellationToken.None);

        // Tell the reader the stream is live straight away — otherwise `curl -N`
        // shows nothing until the first real event, which looks like a hang.
        Send(id, "ready", new { subscribers = _subscribers.Count });
        return id;
    }

    public void Remove(Guid id)
    {
        if (!_subscribers.TryRemove(id, out Subscriber? sub)) return;
        sub.Queue.Writer.TryComplete();
        try { sub.Cts.Cancel(); } catch { /* already disposed */ }
        try { sub.Stream.Dispose(); sub.Response.Close(); } catch { /* already gone */ }
        try { sub.Cts.Dispose(); } catch { /* raced with the pump */ }
    }

    // Emit one named event with a JSON payload to every subscriber. Safe to call
    // from any thread: this only serialises and enqueues.
    public void Send(string eventName, object payload)
    {
        if (_disposed || _subscribers.IsEmpty) return;
        if (TryFrame(eventName, payload) is { } frame) Broadcast(frame);
    }

    private void Send(Guid id, string eventName, object payload)
    {
        if (TryFrame(eventName, payload) is { } frame) Write(id, frame);
    }

    // Serialisation has to be guarded, not just the write. Send runs on the
    // producer's thread — the Telnet read loop, or the UI thread — reached
    // through LogService.EntryAdded, which invokes subscribers unguarded. A
    // throw here (a lone surrogate off the wire reaching a log message, a
    // converter fault) would surface in the pump loop or crash the UI thread,
    // for an event nobody needs that badly.
    private string? TryFrame(string eventName, object payload)
    {
        try
        {
            string json = JsonSerializer.Serialize(payload, LocalApiJson.Compact);
            return $"event: {eventName}\ndata: {json}\n\n";
        }
        catch (Exception ex)
        {
            _log?.Debug(LocalApiServer.LogCategory,
                $"dropped an unserialisable '{eventName}' event ({ex.GetType().Name}).");
            return null;
        }
    }

    private void Broadcast(string frame)
    {
        if (_disposed) return;
        foreach (Guid id in _subscribers.Keys) Write(id, frame);
    }

    // Enqueue only — never blocks, never touches the socket.
    private void Write(Guid id, string frame)
    {
        if (!_subscribers.TryGetValue(id, out Subscriber? sub)) return;
        if (sub.Queue.Writer.TryWrite(Encoding.UTF8.GetBytes(frame))) return;

        // Queue full: the reader is not draining. Retire it rather than grow.
        _log?.Warn(LocalApiServer.LogCategory,
            $"event subscriber fell more than {QueueDepth} frames behind — dropping it.");
        Remove(id);
    }

    // The single writer for one subscriber's stream. Blocking here is fine: this
    // task exists to be blocked so the producer threads aren't.
    private async Task PumpAsync(Guid id, Subscriber sub)
    {
        try
        {
            await foreach (byte[] frame in sub.Queue.Reader.ReadAllAsync(sub.Cts.Token).ConfigureAwait(false))
            {
                await sub.Stream.WriteAsync(frame, sub.Cts.Token).ConfigureAwait(false);
                await sub.Stream.FlushAsync(sub.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* Remove cancelled us */ }
        catch
        {
            // A closed reader is the normal way a subscription ends (curl ^C,
            // process exit). Retire it quietly — this runs on its own task, so
            // nothing here can reach a thread that produced an event.
        }
        finally
        {
            Remove(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeat.Dispose();
        foreach (Guid id in _subscribers.Keys) Remove(id);
        _log?.Debug(LocalApiServer.LogCategory, "event stream disposed.");
    }
}
