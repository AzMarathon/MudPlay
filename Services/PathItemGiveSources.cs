using MudPlay.Game.Map;

namespace MudPlay.Services;

// Where a path item can be asked for: the NPC keyword gives and room-command gives
// ItemSourceIndex found, each resolved to a room and the command to type there.
//
// There are two kinds. A free hand-over costs nothing and is always a source. A
// plain trade hands the item over for one other item (the sleazy shopkeeper's
// glowing key for an opal brooch). It spends something of the user's, so it ranks
// below every other certain source (a free give, a shop, a guaranteed summon) and
// is a source only while what it takes sits unworn in the pack: the game would
// take a worn item too, and a walk doesn't strip what the user has on. Only a key
// is ever traded for.
//
// When a trade may be used is the caller's decision. The give router is handed one
// only for a door key whose trade the user agreed to on the route card that
// started the walk now running, and only the trade for the item that card named.
public sealed class PathItemGiveSources
{
    private readonly ItemSourceIndex _index;
    private readonly BfsMapper _bfs;
    private readonly MovementFilter _filter;
    private readonly Func<int, int> _unwornCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isKey;
    private readonly Func<int, bool> _soldOrSummoned;
    private readonly Func<int, string?> _alwaysDroppedBy;

    // unwornCount: copies in the pack or on the key ring, worn ones left out.
    // isKey: the item is a key by its record. soldOrSummoned: a shop sells the item
    // or a room command summons its guaranteed dropper. alwaysDroppedBy: a monster
    // that drops the item every time.
    public PathItemGiveSources(
        ItemSourceIndex index,
        BfsMapper bfs,
        MovementFilter filter,
        Func<int, int> unwornCount,
        Func<int, string?> itemName,
        Func<int, bool> isKey,
        Func<int, bool> soldOrSummoned,
        Func<int, string?> alwaysDroppedBy)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(unwornCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isKey);
        ArgumentNullException.ThrowIfNull(soldOrSummoned);
        ArgumentNullException.ThrowIfNull(alwaysDroppedBy);
        _index = index;
        _bfs = bfs;
        _filter = filter;
        _unwornCount = unwornCount;
        _itemName = itemName;
        _isKey = isKey;
        _soldOrSummoned = soldOrSummoned;
        _alwaysDroppedBy = alwaysDroppedBy;
    }

    // The free hand-overs of itemId: the deterministic, keyword-carrying awards. A
    // Monster giver becomes `ask <name> <keyword>` at each of its spawn rooms (the
    // full name, which the game always accepts), a Room giver the bare keyword
    // typed verbatim in that room. GiverName is the display name the route card
    // shows ("(ask Gnome Commander)").
    public IReadOnlyList<GiveSource> Free(int itemId) => Resolve(itemId, trades: false);

    // The trades for itemId the character could make right now. Only a key is ever
    // traded for, and only while no other certain source exists, so a trade never
    // stands in for a free give, a purchase or a summon.
    public IReadOnlyList<GiveSource> Trades(int itemId) =>
        !_isKey(itemId) || _soldOrSummoned(itemId) || Free(itemId).Count > 0
            ? Array.Empty<GiveSource>()
            : Resolve(itemId, trades: true);

    // What the give router may act on: the free hand-overs, or failing those the
    // one trade agreed to. agreedTakes is the item the user agreed to hand over;
    // a trade that takes anything else is not offered, though it buys the same key.
    public IReadOnlyList<GiveSource> ForRouter(int itemId, int? agreedTakes)
    {
        IReadOnlyList<GiveSource> free = Free(itemId);
        if (free.Count > 0 || agreedTakes is not int takes) return free;
        return Trades(itemId).Where(t => t.TakesItemId == takes).ToList();
    }

    // The source a route card names for a walk from source to destination, or null
    // when none can be reached: a free hand-over, or failing those (and with
    // offerTrades) the trade it would ask the user to agree to. Shares the give
    // router's selection and prices the detour as the card's walk will walk it
    // (closedGates are the gate items that card's route goes round), so the card
    // names the giver the run visits.
    public GiveSource? Choose(
        int itemId, RoomKey source, RoomKey destination, bool offerTrades,
        IReadOnlyCollection<int>? closedGates = null)
    {
        IReadOnlyList<GiveSource> candidates = Free(itemId);
        if (candidates.Count == 0 && offerTrades) candidates = Trades(itemId);
        return PathItemGiveRouter.TrySelectGiver(
            candidates, source, destination, (a, b) => DetourDistance(a, b, closedGates), out GiveSource best)
            ? best
            : null;
    }

    // Distance used to score a detour a route card would make for a path item. The
    // acquirable gates are suspended, but for the ones the card's route goes round:
    // the walk is priced with the fetched item in hand, and the destination of a
    // walk that needs a key is behind the door the key opens. On the live filter
    // that leg has no length and every source is turned down. The rooms no item
    // makes safe (the lake's sea rooms) stay closed: no walk to a giver goes through them.
    public int? DetourDistance(RoomKey a, RoomKey b, IReadOnlyCollection<int>? closedGates = null)
    {
        using (_filter.SuspendAcquirableGatesExceptUnprotectable(closedGates ?? Array.Empty<int>()))
            return _bfs.DistanceBetween(a, b, _filter);
    }

    // How a key nothing else yields is come by, for a route card and the log:
    // "sleazy shopkeeper trades one for opal brooch, which captain of the guard
    // drops", or "... for your opal brooch" with one in the pack. Null for an
    // item with any other certain source, or no trade.
    public string? TradeNote(int itemId)
    {
        if (!_isKey(itemId) || _soldOrSummoned(itemId) || Free(itemId).Count > 0) return null;

        // Of several trades, the one the character can make now; failing that, one
        // whose item has a sure source to send them to.
        string? droppedNote = null, plainNote = null;
        foreach (ItemGiver g in _index.GiversOf(itemId))
        {
            if (!IsTrade(g)) continue;
            string wanted = _itemName(g.TradeItemId) ?? $"item #{g.TradeItemId}";
            if (_unwornCount(g.TradeItemId) > 0) return $"{g.Name} trades one for your {wanted}";
            if (_alwaysDroppedBy(g.TradeItemId) is { Length: > 0 } monster)
                droppedNote ??= $"{g.Name} trades one for {wanted}, which {monster} drops";
            else
                plainNote ??= $"{g.Name} trades one for {wanted}";
        }
        return droppedNote ?? plainNote;
    }

    private static bool IsTrade(ItemGiver g) =>
        !g.Deterministic && g.TradeItemId > 0 && g.Keyword.Length > 0;

    private IReadOnlyList<GiveSource> Resolve(int itemId, bool trades)
    {
        IReadOnlyList<ItemGiver> givers = _index.GiversOf(itemId);
        if (givers.Count == 0) return Array.Empty<GiveSource>();

        var result = new List<GiveSource>();
        foreach (ItemGiver g in givers)
        {
            int takes = 0;
            string giverName = g.Name;
            if (trades)
            {
                if (!IsTrade(g) || _unwornCount(g.TradeItemId) <= 0) continue;
                takes = g.TradeItemId;
                giverName = $"{g.Name}, in trade for your {_itemName(takes) ?? $"item #{takes}"}";
            }
            else if (!g.Deterministic || g.Keyword.Length == 0)
            {
                continue;
            }

            // A give with a condition on it (alignment, a quest step) is still
            // asked for: whether this character meets it isn't worked out here, and
            // the giver's own refusal ends the wait (user, 2026-10-06).
            List<string>? refusals = null;
            foreach (int number in g.RefusalMessages ?? Array.Empty<int>())
                if (GiveRefusalLines.For(number) is { } line)
                    (refusals ??= new List<string>()).Add(line);

            if (g.Kind == ItemGiverKind.Monster)
            {
                string noun = GuardDoorCommandResolver.AskTarget(g.Name);
                if (noun.Length == 0) continue;   // no addressable name — can't ask
                string command = $"ask {noun} {g.Keyword}";
                foreach (RoomKey room in _index.GiverMonsterRoomsOf(g.Number))
                    result.Add(new GiveSource(room, command, giverName, refusals, takes));
            }
            else // Room giver — the keyword is the verbatim room CMD.
            {
                result.Add(new GiveSource(new RoomKey(g.Map, g.Room), g.Keyword, giverName, refusals, takes));
            }
        }
        return result;
    }
}
