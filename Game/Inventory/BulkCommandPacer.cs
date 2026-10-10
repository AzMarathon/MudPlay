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
// _execute_input / _add_delayed_command). drop, get, sell, buy and `hide <item>`
// add no delay of their own (only the bare `hide` does), so a sweep queues only
// behind a delay that was already running.
//
// So at most Window commands are left unanswered — two short of the warning — and
// each prompt the game sends back answers one. A small gap between sends keeps a
// stray prompt (a regen tick, a combat round) from releasing the rest in a burst.
// Paradigm's limit is still to be measured; it uses the same pacing.
//
// The window is what keeps the queue safe; the gap only sets the top speed. At
// 150 ms a sweep ran about six commands a second, half the speed of a gear set,
// which sends its commands all at once (report paradigm-20261006-112434: 38 drops
// in 6 s, 14 wears in 1 s, none refused).
public sealed class BulkCommandPacer
{
    public const int Window = 6;
    public static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(50);
    // A prompt can go unseen (a disconnect, a statline the scanner can't read); after
    // this long with nothing sent or answered, the window is treated as drained.
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(3);
    // After the game said it ignored a command, give its queue time to empty.
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(3);

    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // Each waiting command with whoever asked to be able to take it back, if anyone.
    private readonly Queue<(string Command, object? Owner)> _queue = new();
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

    // An owner marks the commands as its own to take back while they wait
    // (CancelOwned). Commands without one are only ever dropped by Cancel.
    public void Enqueue(IEnumerable<string> commands, object? owner = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (string command in commands) _queue.Enqueue((command, owner));
        Pump();
    }

    // Take back one owner's waiting commands that `take` picks, leaving everyone
    // else's and the order of the rest. `take` is asked front to back, once per
    // command. Returns what was taken; a command already sent is not in reach.
    public IReadOnlyList<string> CancelOwned(object owner, Func<string, bool> take)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(take);
        List<string> taken = new();
        for (int n = _queue.Count; n > 0; n--)
        {
            (string Command, object? Owner) waiting = _queue.Dequeue();
            if (ReferenceEquals(waiting.Owner, owner) && take(waiting.Command)) taken.Add(waiting.Command);
            else _queue.Enqueue(waiting);
        }
        return taken;
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

            _send(_queue.Dequeue().Command);
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
