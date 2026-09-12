using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MudPlay.Services.Api;

// Server-Sent Events fan-out for the local API. One subscriber per open
// GET /events response; each gets every event until its socket dies.
//
// SSE rather than WebSockets because the traffic is one-directional (the client
// only listens) and SSE is plain chunked HTTP — no framing library, no extra
// dependency, and `curl -N` can read it directly, which matters when the whole
// point is diagnosing a wedged client by hand.
//
// The subscriber list is the reason this is its own type: writes come from
// wherever the event originated (LogService fires on the producer's thread,
// gates fire on the UI thread) and must not block that thread on a slow reader.
// Every write is therefore fire-and-forget onto the response stream, and a
// failed write retires the subscriber rather than propagating.
public sealed class LocalApiEvents : IDisposable
{
    // Comment line every this often. Keeps intermediaries from timing the
    // connection out, and lets a reader notice a dead server during a long quiet
    // stretch — which is exactly the situation being diagnosed.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly LogService? _log;
    private readonly Timer _heartbeat;
    private bool _disposed;

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

    private sealed record Subscriber(HttpListenerResponse Response, Stream Stream);

    // Adopt a response as an SSE stream. Returns the id so the request loop can
    // retire it when the handler unwinds.
    public Guid Add(HttpListenerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        // No Content-Length: the body is open-ended, so the response must be
        // chunked. SendChunked also stops HttpListener buffering our writes.
        response.SendChunked = true;

        Guid id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(response, response.OutputStream);
        // Tell the reader the stream is live straight away — otherwise `curl -N`
        // shows nothing until the first real event, which looks like a hang.
        Send(id, "ready", new { subscribers = _subscribers.Count });
        return id;
    }

    public void Remove(Guid id)
    {
        if (!_subscribers.TryRemove(id, out Subscriber? sub)) return;
        try { sub.Stream.Dispose(); sub.Response.Close(); } catch { /* already gone */ }
    }

    // Emit one named event with a JSON payload to every subscriber.
    public void Send(string eventName, object payload)
    {
        if (_disposed || _subscribers.IsEmpty) return;
        string json = JsonSerializer.Serialize(payload, LocalApiJson.Compact);
        Broadcast($"event: {eventName}\ndata: {json}\n\n");
    }

    private void Send(Guid id, string eventName, object payload)
    {
        string json = JsonSerializer.Serialize(payload, LocalApiJson.Compact);
        Write(id, $"event: {eventName}\ndata: {json}\n\n");
    }

    private void Broadcast(string frame)
    {
        if (_disposed) return;
        foreach (Guid id in _subscribers.Keys) Write(id, frame);
    }

    private void Write(Guid id, string frame)
    {
        if (!_subscribers.TryGetValue(id, out Subscriber? sub)) return;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(frame);
            sub.Stream.Write(bytes, 0, bytes.Length);
            sub.Stream.Flush();
        }
        catch
        {
            // A closed reader is the normal way a subscription ends (curl ^C,
            // process exit). Retire it quietly — an exception here must never
            // reach the thread that produced the event.
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
