namespace MudPlay.Game.Map;

// What a journey was told to fetch on its way, whatever the items' Auto-obtain
// flags say, and the trades a route card named for it. It rides on the WalkJourney
// and has no life of its own: it is there for the legs, side trips and restarts
// of that journey, and gone with it.
//
// A trade hands an item of the user's over, so it is held as a pair, the door key
// and the item given up for it: the walk makes that trade and no other that would
// buy the same key. It also belongs to the connection it was agreed on. After a
// drop nobody can say where the character stands or how far a trade had got, so
// a walk picked up again after reconnecting fetches as before but trades nothing.
public sealed class JourneyFetch
{
    private readonly HashSet<int> _items = new();
    private readonly Dictionary<int, int> _trades = new();
    private readonly int _session;

    // session: the connection the trades were agreed on (see AgreedTradeFor).
    public JourneyFetch(
        IEnumerable<int> items, IEnumerable<(int KeyId, int TakesItemId)>? trades = null, int session = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (int id in items) if (id > 0) _items.Add(id);
        foreach ((int keyId, int takes) in trades ?? Array.Empty<(int, int)>())
            if (keyId > 0 && takes > 0) _trades[keyId] = takes;
        _session = session;
    }

    public bool HasItems => _items.Count > 0;
    public IReadOnlyCollection<int> Items => _items;
    public bool Fetches(int itemId) => _items.Contains(itemId);

    // Door key → the item handed over for it (bug report).
    public IReadOnlyDictionary<int, int> Trades => _trades;

    // The item agreed to be handed over for keyId, or null: only for a key still
    // being fetched (once it is in hand there is nothing left to trade for), and
    // only on the connection the agreement was made on.
    public int? AgreedTradeFor(int keyId, int session) =>
        session == _session && _items.Contains(keyId) && _trades.TryGetValue(keyId, out int takes) ? takes : null;

    // Drop the items now covered; the journey has what it wanted of them.
    public void DropCovered(Func<int, bool> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);
        _items.RemoveWhere(id => covered(id));
    }
}
