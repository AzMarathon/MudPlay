namespace MudPlay.Game.Inventory;

// The inventory read a refused toll asks for. The refusal shows the purse on record
// is wrong, and until a full `i` is read no toll or fare is taken, so the read must
// not be lost: asked while the master switch is off (the user's own move, then) or
// while the send gate is held, it stays owed and goes out when that ends. One `i`
// per refusal, and none once a read has come by another road.
public sealed class OwedPurseRead
{
    private readonly Func<bool> _held;
    private readonly Action _send;
    private bool _owed;

    // held: nothing may be sent now (master switch off, send gate held).
    public OwedPurseRead(Func<bool> held, Action send)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(send);
        _held = held;
        _send = send;
    }

    // True while a read was asked for and hasn't gone out.
    public bool Owed => _owed;

    public void Ask()
    {
        if (_held())
        {
            _owed = true;
            return;
        }
        _owed = false;
        _send();
    }

    // The master switch came back on, or the send gate let go.
    public void Retry()
    {
        if (_owed) Ask();
    }

    // A full inventory read arrived, whoever asked for it; or another character
    // was loaded.
    public void Settle() => _owed = false;
}
