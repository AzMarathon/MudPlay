namespace MudPlay.Game.Inventory;

// The inventory read a refused toll asks for. The refusal shows the purse on record
// is wrong, and until a full `i` is read no toll or fare is taken, so the read must
// not be lost: asked while the master switch is off (the user's own move, then),
// outside the game or while the send gate is held, it stays owed and goes out when
// that ends. One `i` per refusal, and none once a read has come by another road.
//
// The walk or loop that was refused re-plans, and what it plans depends on the
// answer: planned before it, every toll is closed and a trip the purse could pay
// for stops (user, 2026-10-10: resolve the purse first). WaitForAnswer holds that
// re-plan until the read lands, for no longer than AnswerBound.
public sealed class OwedPurseRead
{
    // The bound the `rm` position ask has (ParadigmPositionResolver): past it the
    // re-plan goes ahead on the purse in doubt, as it did before it waited.
    public static readonly TimeSpan AnswerBound = TimeSpan.FromSeconds(3);

    private readonly Func<bool> _held;
    private readonly Action _send;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly List<Action> _waiting = new();
    private IDisposable? _bound;
    private bool _owed;
    private bool _unanswered;

    // held: nothing may be sent now (master switch off, not in the game, send gate
    // held). schedule: a one-shot on the thread the lines are read on.
    public OwedPurseRead(Func<bool> held, Action send, Func<TimeSpan, Action, IDisposable> schedule)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(schedule);
        _held = held;
        _send = send;
        _schedule = schedule;
    }

    // True while a read was asked for and hasn't gone out.
    public bool Owed => _owed;

    public void Ask()
    {
        _unanswered = true;
        if (_held())
        {
            _owed = true;
            return;
        }
        _owed = false;
        _send();
    }

    // The master switch came back on, the game was entered, or the send gate let go.
    public void Retry()
    {
        if (_owed) Ask();
    }

    // Another `i` has just gone out (the one a death asks for): it reads the purse
    // too, so this one is not sent as well. Its answer is still awaited.
    public void CoveredByAnotherRead() => _owed = false;

    // A full inventory read arrived, whoever asked for it; or another character
    // was loaded.
    public void Settle()
    {
        _owed = false;
        _unanswered = false;
        Release();
    }

    // For a re-plan the read's answer would change. True when a read is still
    // unanswered: `then` runs when it answers or the bound passes. False when none
    // is, and the caller goes ahead at once.
    public bool WaitForAnswer(Action then)
    {
        ArgumentNullException.ThrowIfNull(then);
        if (!_unanswered) return false;
        _waiting.Add(then);
        _bound ??= _schedule(AnswerBound, Release);
        return true;
    }

    private void Release()
    {
        _bound?.Dispose();
        _bound = null;
        if (_waiting.Count == 0) return;
        Action[] run = _waiting.ToArray();
        _waiting.Clear();
        foreach (Action then in run) then();
    }
}
