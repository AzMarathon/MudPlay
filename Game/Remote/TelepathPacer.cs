using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Remote;

// Every outgoing telepath goes through here on its way to the socket.
//
// The server throttles telepaths: several fired together and the later ones are
// refused. It answers each telepath, in the order they were sent, with either
// "--- Telepath Sent to <Name> ---" (delivered) or "--- Telepath Not Sent ---"
// (dropped by the throttle). Joining a party is the classic burst — both clients
// probe each other (@health / @level / @version) and answer each other's probes
// at the same instant — and a dropped probe left a stale player record all day.
//
// So telepath lines (`/<name> …`) are held in a queue and written at least Gap
// apart, and each is kept "in flight" until its acknowledgement comes back. A
// "Not Sent" puts the oldest in-flight telepath back at the head of the queue
// (MaxAttempts total); a "Sent to X" retires it. Everything else — movement,
// combat, casts — is written straight through, never delayed behind a telepath.
//
// Acks are matched in order, which holds for everything that passes through here.
// A telepath that didn't (typed key-by-key in direct-input mode) can still produce
// an ack: a "Sent to" whose name doesn't match the oldest in-flight target is
// taken as that stray and ignored; an in-flight entry with no ack after AckTimeout
// is given up on so a missing ack can't shift every later match.
public sealed partial class TelepathPacer
{
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(100);
    public const int MaxAttempts = 3;
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);

    // The ack lines, alone on a line or glued after the prompt ("[HP=32/MA=27]:---
    // Telepath Sent to Raijin ---"). Anchored so a chat line QUOTING one ("Bob
    // gossips: --- Telepath Not Sent ---") can't make us resend anything.
    [GeneratedRegex(@"^(?:\[[^\]]*\]:)?\s*--- Telepath Sent to (?<name>\S+) ---\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentAck();

    [GeneratedRegex(@"^(?:\[[^\]]*\]:)?\s*--- Telepath Not Sent ---\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NotSentAck();

    private sealed class Telepath(byte[] bytes, string target)
    {
        public byte[] Bytes { get; } = bytes;
        public string Target { get; } = target;
        public int Attempts { get; set; }
        public DateTimeOffset SentAt { get; set; }
    }

    private readonly Func<DateTimeOffset> _now;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly LogService? _log;
    private readonly object _lock = new();
    private readonly LinkedList<Telepath> _outbox = new();   // resends go to the head
    private readonly Queue<Telepath> _inFlight = new();
    private Action<byte[]>? _write;
    private LineExtractor? _lines;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private bool _pumpArmed;
    // Resends waiting at the head of the outbox. A new resend goes after them, so two
    // refusals acknowledged back to back are resent in their original order.
    private int _resendsQueued;
    private int _resends;
    private int _givenUp;

    public TelepathPacer(Action<TimeSpan, Action> armTimer, Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _now = now ?? (() => DateTimeOffset.Now);
        _log = log;
    }

    // The raw socket write — bound by the main window VM with the live connection.
    public void SetWriter(Action<byte[]> write) => _write = write ?? throw new ArgumentNullException(nameof(write));

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Bug-report surface.
    public int Queued { get { lock (_lock) return _outbox.Count; } }
    public int InFlight { get { lock (_lock) return _inFlight.Count; } }
    public int Resends { get { lock (_lock) return _resends; } }
    public int GivenUp { get { lock (_lock) return _givenUp; } }

    // Hand an outbound buffer to the wire: complete telepath lines are queued and
    // paced, everything else is written at once, in order.
    public void Send(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<byte> passThrough = new(data.Length);
        int start = 0;
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\r') continue;
                int end = i + 1;
                if (end < data.Length && data[end] == (byte)'\n') { end++; i++; }
                byte[] line = data[start..end];
                if (TelepathTarget(line) is { } target) _outbox.AddLast(new Telepath(line, target));
                else passThrough.AddRange(line);
                start = end;
            }
            if (start < data.Length) passThrough.AddRange(data[start..]);   // a partial line (direct input)
        }
        if (passThrough.Count > 0) _write?.Invoke(passThrough.ToArray());
        Pump();
    }

    // `/<name> <text>` — the telepath form every sender here uses.
    internal static string? TelepathTarget(byte[] line)
    {
        if (line.Length < 3 || line[0] != (byte)'/' || !char.IsLetter((char)line[1])) return null;
        int end = 1;
        while (end < line.Length && line[end] != (byte)' ' && line[end] != (byte)'\r') end++;
        return Encoding.Latin1.GetString(line, 1, end - 1);
    }

    private void Pump()
    {
        Telepath? next = null;
        TimeSpan wait = TimeSpan.Zero;
        lock (_lock)
        {
            if (_outbox.First is null || _pumpArmed) return;
            DateTimeOffset now = _now();
            DateTimeOffset due = _lastSent + Gap;
            if (now < due)
            {
                wait = due - now;
                _pumpArmed = true;
            }
            else
            {
                next = _outbox.First.Value;
                _outbox.RemoveFirst();
                if (_resendsQueued > 0) _resendsQueued--;
                next.Attempts++;
                next.SentAt = now;
                _inFlight.Enqueue(next);
                _lastSent = now;
                if (_outbox.First is not null)
                {
                    wait = Gap;
                    _pumpArmed = true;
                }
            }
        }
        if (next is not null) _write?.Invoke(next.Bytes);
        if (wait > TimeSpan.Zero)
            _armTimer(wait, () =>
            {
                lock (_lock) _pumpArmed = false;
                Pump();
            });
    }

    private void OnLine(LineExtractor.EmittedLine line) => OnAck(line.Text);

    internal void OnAck(string text)
    {
        bool sent;
        string? name = null;
        Match m = SentAck().Match(text);
        if (m.Success) { sent = true; name = m.Groups["name"].Value; }
        else if (NotSentAck().IsMatch(text)) sent = false;
        else return;

        bool resend = false;
        lock (_lock)
        {
            ExpireUnacked();
            if (_inFlight.Count == 0) return;
            Telepath oldest = _inFlight.Peek();
            if (sent)
            {
                // "/raij" reaches Raijin — the ack names the full player.
                if (name!.StartsWith(oldest.Target, StringComparison.OrdinalIgnoreCase)) _inFlight.Dequeue();
                return;
            }
            _inFlight.Dequeue();
            if (oldest.Attempts >= MaxAttempts)
            {
                _givenUp++;
                _log?.Info("Telepath", $"Telepath to {oldest.Target} not sent after {MaxAttempts} tries — giving up.");
                return;
            }
            _resends++;
            // Ahead of anything never sent, behind resends already waiting.
            LinkedListNode<Telepath>? after = null;
            for (int i = 0; i < _resendsQueued; i++) after = after is null ? _outbox.First : after.Next;
            if (after is null) _outbox.AddFirst(oldest);
            else _outbox.AddAfter(after, oldest);
            _resendsQueued++;
            _log?.Info("Telepath", $"Telepath to {oldest.Target} not sent (throttled) — resending (try {oldest.Attempts + 1}/{MaxAttempts}).");
            resend = true;
        }
        if (resend) Pump();
    }

    private void ExpireUnacked()
    {
        DateTimeOffset cutoff = _now() - AckTimeout;
        while (_inFlight.Count > 0 && _inFlight.Peek().SentAt < cutoff) _inFlight.Dequeue();
    }
}
