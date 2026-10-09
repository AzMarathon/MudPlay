namespace MudPlay.Game.Map;

// What the pick of a route card arranged for the one walk it starts: the items to
// fetch on the way whatever their Auto-obtain flag says, and the trades the card
// named. A trade hands an item of the user's over, so it belongs to that walk and
// to no other. AgreedTradeFor is the only way anything learns of a trade, and it
// answers for the walk now running:
//
//   - not before the walk the card committed has started, and only if that walk
//     is to the destination the card was for;
//   - not after that walk has ended, however it ended: arrived, failed, stopped,
//     or pushed aside by a walk that isn't one of its own fetch detours;
//   - not after End, which death, a dropped connection, a profile change, Stop and
//     an engine reset call. Those can happen with the walker standing idle (it
//     waits at a giver for the hand-over), where no walk event would tell.
//
// The order is put together before its walk exists: a pick states its trades and
// items, then commits the walk. A pick made during another walk stops that walk
// on the way, and the Stopped it raises is the old walk's, not this one's. So an
// order waiting for its walk ignores it.
//
// The fetch list is looser than the trades, as it always was: a detour of some
// other engine in the middle of the walk leaves it standing, since losing it would
// leave the walk at a gate with nothing arranged.
public sealed class RouteCardFetch
{
    private readonly HashSet<int> _items = new();
    // Door key → the item handed over for it. A pair, so the walk can't hand over
    // some other item that happens to buy the same key.
    private readonly Dictionary<int, int> _trades = new();
    private RoomKey? _tradeDestination;
    private bool _pickOpen;
    private bool _walkStarted;
    private RoomKey? _walkDestination;
    // A fetch detour's target, announced just before its walk starts.
    private RoomKey? _announcedLeg;
    // Where the walker is heading for this walk right now: its destination or a
    // fetch detour's stop. Null while it stands idle at such a stop. A start to the
    // same place is the walk re-planning itself (or a start announced twice, as one
    // that waits for moves to settle is); from idle only an announced leg is ours.
    private RoomKey? _heading;

    // Raised when an order that held anything is dropped, with why.
    public event Action<string>? Ended;

    public bool HasItems => _items.Count > 0;
    public IReadOnlyCollection<int> Items => _items;
    public bool Fetches(int itemId) => _items.Contains(itemId);

    // The trades held, and whether their walk has started (bug report).
    public IReadOnlyDictionary<int, int> Trades => _trades;
    public bool WalkStarted => _walkStarted;

    // The item agreed to be handed over for keyId on the walk now running, or null.
    public int? AgreedTradeFor(int keyId) =>
        _walkStarted && _items.Contains(keyId) && _trades.TryGetValue(keyId, out int takes) ? takes : null;

    // A route card's pick opens its order: the walk it is about to commit goes to
    // destination, and these are the trades the card named (key, item handed over).
    // Replaces whatever was held. Fetch then adds the pick's items.
    public void AgreeTrades(RoomKey destination, IReadOnlyList<(int KeyId, int TakesItemId)> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        Clear();
        _pickOpen = true;
        _tradeDestination = destination;
        foreach ((int keyId, int takes) in trades)
            if (keyId > 0 && takes > 0) _trades[keyId] = takes;
    }

    // Items the next walk is to fetch. Within a pick AgreeTrades opened they add
    // up, so a pick can order its hazard counters and its gate items in two goes.
    // Any other caller starts a fresh order, with no trades.
    public void Fetch(IEnumerable<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_pickOpen) Clear();
        foreach (int id in itemIds) if (id > 0) _items.Add(id);
    }

    // Drop the items now covered; the walk has what it wanted of them.
    public void DropCovered(Func<int, bool> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);
        _items.RemoveWhere(id => covered(id));
    }

    // A fetch detour is about to take the walker to target and hand it back: that
    // walk is a leg of this one, not another walk replacing it.
    public void NoteOwnLeg(RoomKey target) => _announcedLeg = target;

    public void OnWalkEvent(WalkEvent e)
    {
        if (e.Kind is not (WalkEventKind.Started or WalkEventKind.Finished
            or WalkEventKind.Stopped or WalkEventKind.Failed))
            return;

        _pickOpen = false;
        RoomKey? announced = _announcedLeg;
        _announcedLeg = null;
        if (_items.Count == 0 && _trades.Count == 0) return;

        if (!_walkStarted)
        {
            switch (e.Kind)
            {
                case WalkEventKind.Started:
                    _walkStarted = true;
                    _walkDestination = e.Destination;
                    _heading = e.Destination;
                    // Some other walk set off in place of the one the card was for.
                    if (_trades.Count > 0 && e.Destination != _tradeDestination) _trades.Clear();
                    break;
                case WalkEventKind.Finished:
                case WalkEventKind.Failed:
                    End("its walk didn't start");
                    break;
                // Stopped: the walk this pick replaces. Not ours.
            }
            return;
        }

        switch (e.Kind)
        {
            case WalkEventKind.Started:
                // A fetch detour and the walk back from it announce themselves, and
                // a re-plan heads where the walk was already heading. Anything else
                // took the walker, wherever it is going.
                bool ours = e.Destination is { } to && (to == announced || to == _heading);
                if (!ours) _trades.Clear();
                _heading = e.Destination;
                break;
            case WalkEventKind.Finished:
                if (e.Destination == _walkDestination) End("its walk arrived");
                else _heading = null;
                break;
            case WalkEventKind.Stopped:
                End("its walk was stopped");
                break;
            case WalkEventKind.Failed:
                End("its walk failed");
                break;
        }
    }

    // Drop everything. Safe to call with nothing held.
    public void End(string reason)
    {
        bool held = _items.Count > 0 || _trades.Count > 0;
        Clear();
        if (held) Ended?.Invoke(reason);
    }

    private void Clear()
    {
        _items.Clear();
        _trades.Clear();
        _tradeDestination = null;
        _pickOpen = false;
        _walkStarted = false;
        _walkDestination = null;
        _announcedLeg = null;
        _heading = null;
    }
}
