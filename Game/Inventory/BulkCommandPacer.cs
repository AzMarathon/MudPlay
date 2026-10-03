using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Sends a sweep of item commands (drop-all, hide-all, get-all) a few at a time, so
// a long sweep can't overflow the game's command queue.
//
// On Stock the engine runs a command at once unless the character is under an
// action delay (after a move, sneak, search, hide, …) or already has commands
// waiting; then the command joins a per-character queue. With 8 waiting it warns
// "Why don't you slow down for a few seconds?", and from 12 it drops the newest
// with "You are typing too quickly - command ignored" (wccmmud.dll 1.11p
// _execute_input / _add_delayed_command). drop, get, sell and buy add no delay of
// their own, but hide adds one, so a hide sweep queues behind itself.
//
// So at most Window commands are left unanswered — two short of the warning — and
// each prompt the game sends back answers one. A small gap between sends keeps a
// stray prompt (a regen tick, a combat round) from releasing the rest in a burst.
// Paradigm's limit is still to be measured; it uses the same pacing.
public sealed class BulkCommandPacer
{
    public const int Window = 6;
    public static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(150);
    // A prompt can go unseen (a disconnect, a statline the scanner can't read); after
    // this long with nothing sent or answered, the window is treated as drained.
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(3);
    // After the game said it ignored a command, give its queue time to empty.
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(3);

    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private readonly Queue<string> _queue = new();
    private int _unanswered;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private DateTimeOffset _holdUntil = DateTimeOffset.MinValue;
    private bool _timerArmed;

    public BulkCommandPacer(Action<string> send, Action<TimeSpan, Action> scheduleAfter,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(scheduleAfter);
        _send = send;
        _scheduleAfter = scheduleAfter;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Commands still waiting to be sent.
    public int Pending => _queue.Count;

    public void Enqueue(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (string command in commands) _queue.Enqueue(command);
        Pump();
    }

    // The game sent a prompt: one command has been answered.
    public void NotePrompt()
    {
        if (_unanswered > 0)
        {
            _unanswered--;
            _lastActivity = _now();
        }
        Pump();
    }

    // The game ignored a command. Which one can't be told, so nothing is re-sent
    // (a second `drop` of a stack drops another copy); the rest of the sweep waits
    // for the queue to empty.
    public void NoteRateLimited()
    {
        if (_queue.Count == 0 && _unanswered == 0) return;
        _log?.Warn("Inventory", $"the game ignored a command while {_queue.Count} more were waiting - pausing the sweep, one item may have been skipped");
        _unanswered = 0;
        _holdUntil = _now() + RateLimitBackoff;
        Pump();
    }

    public void Cancel()
    {
        _queue.Clear();
        _unanswered = 0;
    }

    private void Pump()
    {
        while (_queue.Count > 0)
        {
            DateTimeOffset now = _now();
            if (now < _holdUntil) { Arm(_holdUntil - now); return; }
            if (_unanswered >= Window)
            {
                // Nothing answered for a while: the prompts were missed, not owed.
                if (now - _lastActivity >= AnswerTimeout) _unanswered = 0;
                else { Arm(AnswerTimeout - (now - _lastActivity)); return; }
            }
            TimeSpan sinceLast = now - _lastSend;
            if (sinceLast < MinGap) { Arm(MinGap - sinceLast); return; }

            _send(_queue.Dequeue());
            _unanswered++;
            _lastSend = now;
            _lastActivity = now;
        }
    }

    private void Arm(TimeSpan delay)
    {
        if (_timerArmed) return;
        _timerArmed = true;
        _scheduleAfter(delay, () =>
        {
            _timerArmed = false;
            Pump();
        });
    }
}
