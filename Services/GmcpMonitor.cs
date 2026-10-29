using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MudPlay.Net;
using MudPlay.Terminal;

namespace MudPlay.Services;

// Records the server's side of a GMCP session from each handshake on: every GMCP
// message, both ways, and the server's text in between, in the order they came.
// Which came first is what the client's decisions turn on, so the export keeps it,
// in the capture format the tests replay (MudPlay.Tests/Gmcp/Captured). What we
// type is never recorded: it carries the login and every command.
//
// Thread-safe: the telnet read loop records, the window reads.
public sealed class GmcpMonitor
{
    // Frames kept per session; the oldest go first.
    public const int Capacity = 20_000;

    public readonly record struct Frame(long Milliseconds, bool FromServer, string? Package, string Body);

    private readonly object _gate = new();
    private readonly Queue<Frame> _frames = new();
    private readonly Stopwatch _clock = new();
    private bool _recording;
    private int _dropped;

    // The handshake that started the recording, local time; null before the first.
    public DateTimeOffset? Since { get; private set; }

    public event Action? Changed;

    // A new handshake starts a new recording; GMCP switched off ends it, keeping
    // what was recorded for export.
    public void NoteHandshake(bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                _frames.Clear();
                _dropped = 0;
                _clock.Restart();
                Since = DateTimeOffset.Now;
            }
            _recording = enabled;
        }
        Changed?.Invoke();
    }

    public void NoteReceived(GmcpMessage message) =>
        Record(new Frame(0, FromServer: true, message.Package, message.Payload.GetRawText()));

    public void NoteSent(string package, object payload) =>
        Record(new Frame(0, FromServer: false, package, JsonSerializer.Serialize(payload)));

    // The server's text: ASCII as itself, escape sequences and line ends included,
    // and the upper half as the CP437 glyphs the terminal draws (the inverse of
    // Cp437.Encode, so a replay feeds back the same bytes).
    public void NoteText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        StringBuilder text = new(data.Length);
        foreach (byte b in data) text.Append(b < 0x80 ? (char)b : Cp437.ToUnicode(b));
        Record(new Frame(0, FromServer: true, Package: null, text.ToString()));
    }

    private void Record(Frame frame)
    {
        lock (_gate)
        {
            if (!_recording) return;
            _frames.Enqueue(frame with { Milliseconds = _clock.ElapsedMilliseconds });
            if (_frames.Count > Capacity)
            {
                _frames.Dequeue();
                _dropped++;
            }
        }
        Changed?.Invoke();
    }

    public bool IsRecording
    {
        get { lock (_gate) return _recording; }
    }

    // Frames that fell out of a long session's recording.
    public int Dropped
    {
        get { lock (_gate) return _dropped; }
    }

    public IReadOnlyList<Frame> Snapshot()
    {
        lock (_gate) return _frames.ToArray();
    }

    // The recording as capture lines: a header, then one frame per line. scrub masks
    // the saved login and the like, as a bug report's does.
    public string Export(Func<string, string> scrub, string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(scrub);
        IReadOnlyList<Frame> frames = Snapshot();
        StringBuilder lines = new();
        lines.AppendLine(JsonSerializer.Serialize(new
        {
            recording = 1,
            source = "mudplay-monitor",
            client = clientVersion,
            captured = (Since ?? DateTimeOffset.Now).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            dropped = Dropped,
            fixtureSanitization = "Client-to-server text (login credentials and commands) omitted; saved login masked.",
        }));
        // The recording starts at the handshake, so a replay negotiates first.
        lines.AppendLine(JsonSerializer.Serialize(new { t = 0, dir = "s2c", kind = "negotiation", telnet = "IAC WILL GMCP" }));
        foreach (Frame frame in frames)
        {
            string direction = frame.FromServer ? "s2c" : "c2s";
            string body = scrub(frame.Body);
            if (frame.Package is { } package)
            {
                // A masked body may no longer be JSON; it's kept as text then.
                bool json = TryParse(body, out JsonElement message);
                lines.AppendLine(json
                    ? JsonSerializer.Serialize(new { t = frame.Milliseconds, dir = direction, kind = "gmcp", pkg = package, msg = message })
                    : JsonSerializer.Serialize(new { t = frame.Milliseconds, dir = direction, kind = "gmcp", pkg = package, text = body }));
            }
            else
            {
                lines.AppendLine(JsonSerializer.Serialize(new { t = frame.Milliseconds, dir = direction, kind = "text", text = body }));
            }
        }
        return lines.ToString();
    }

    private static bool TryParse(string body, out JsonElement value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
