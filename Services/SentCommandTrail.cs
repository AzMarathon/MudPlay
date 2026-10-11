using System.Text;

namespace MudPlay.Services;

// The last commands the client put on the wire, or meant to: when, what, where
// it came from and what became of it. A bug report prints it, because "why did it
// send that" and "why did it send nothing" are otherwise answered only by the
// game's echo, which names no sender and shows nothing for a command that was
// dropped.
//
// Filled at the places every command already passes: EngineSendGate's wrapper
// for what the client sends, MainWindowViewModel.SendUserInput for what is typed.
//
// Nothing is recorded while Withhold says so: outside the game (a login is typed
// there) and while a suicide password may be going out. Those lines are counted,
// never kept.
public sealed class SentCommandTrail
{
    // Sized from the bug report's budget: ~70 bytes an entry, so the whole ring
    // prints in about 35 KB and reaches back several minutes of a busy session.
    public const int Capacity = 500;

    // A longer line is a paste, not a command; its head is enough to place it.
    private const int MaxTextLength = 160;

    public enum Origin
    {
        Typed,          // the user, at the terminal
        Engine,         // an engine decided it
        UserRelayed,    // a macro, alias, trigger or event of the user's
        RouteStep,      // a walk or loop step
    }

    public enum Outcome { Sent, HeldForLater, Dropped }

    public readonly record struct Entry(
        DateTimeOffset At, string Text, Origin Origin, string? Sender, Outcome Outcome, string? Why);

    private readonly object _gate = new();
    private readonly Entry[] _ring = new Entry[Capacity];
    private int _head;
    private int _count;
    private int _withheld;
    private readonly StringBuilder _typedLine = new();

    // True while a line must not be kept. Null in tests that don't care.
    public Func<bool>? Withhold { get; set; }

    // Lines that went by while Withhold was true.
    public int Withheld { get { lock (_gate) { return _withheld; } } }

    public void Record(string text, Origin origin, string? sender, Outcome outcome, string? why = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Withhold?.Invoke() == true)
        {
            lock (_gate) { _withheld++; }
            return;
        }
        if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…";
        Entry entry = new(DateTimeOffset.Now, text, origin, sender, outcome, why);
        lock (_gate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    // Bytes the user typed. The terminal sends a whole line at a time, or a key
    // at a time while a form owns the keyboard: either way an entry is one line,
    // made when its Enter goes out. Keys that aren't text (arrows, escapes) are
    // not part of it.
    public void ObserveTyped(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            switch (b)
            {
                case 0x0D or 0x0A:
                    if (_typedLine.Length > 0)
                    {
                        Record(_typedLine.ToString(), Origin.Typed, sender: null, Outcome.Sent);
                        _typedLine.Clear();
                    }
                    break;
                case 0x08 or 0x7F:
                    if (_typedLine.Length > 0) _typedLine.Length--;
                    break;
                case < 0x20:
                    break;
                default:
                    if (_typedLine.Length <= MaxTextLength) _typedLine.Append((char)b);
                    break;
            }
        }
    }

    // Oldest first.
    public Entry[] Snapshot()
    {
        lock (_gate)
        {
            Entry[] result = new Entry[_count];
            int start = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++) result[i] = _ring[(start + i) % Capacity];
            return result;
        }
    }
}
