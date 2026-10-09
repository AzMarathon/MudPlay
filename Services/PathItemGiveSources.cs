using MudPlay.Game.Map;

namespace MudPlay.Services;

// Where a path item can be asked for: the NPC keyword gives and room-command gives
// ItemSourceIndex found, each resolved to a room and the command to type there.
//
// There are two kinds. A free hand-over costs nothing and is always a source. A
// plain trade hands the item over for one other item (the sleazy shopkeeper's
// glowing key for an opal brooch). It spends something of the user's, so it ranks
// below every other certain source (a free give, a shop, a guaranteed summon) and
// is a source only while what it takes sits unworn in the pack: whether the game
// takes a worn item in a trade isn't known.
//
// When a trade may be used is the caller's decision. The give router is handed one
// only for a door key whose trade the user agreed to on a route card that named it.
public sealed class PathItemGiveSources
{
    private readonly ItemSourceIndex _index;
    private readonly BfsMapper _bfs;
    private readonly MovementFilter _filter;
    private readonly Func<int, int> _unwornCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _soldOrSummoned;
    private readonly Func<int, string?> _alwaysDroppedBy;

    // unwornCount: copies in the pack or on the key ring, worn ones left out.
    // soldOrSummoned: a shop sells the item or a room command summons its
    // guaranteed dropper. alwaysDroppedBy: a monster that drops the item every time.
    public PathItemGiveSources(
        ItemSourceIndex index,
        BfsMapper bfs,
        MovementFilter filter,
        Func<int, int> unwornCount,
        Func<int, string?> itemName,
        Func<int, bool> soldOrSummoned,
        Func<int, string?> alwaysDroppedBy)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(unwornCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(soldOrSummoned);
        ArgumentNullException.ThrowIfNull(alwaysDroppedBy);
        _index = index;
        _bfs = bfs;
        _filter = filter;
        _unwornCount = unwornCount;
        _itemName = itemName;
        _soldOrSummoned = soldOrSummoned;
        _alwaysDroppedBy = alwaysDroppedBy;
    }

    // The free hand-overs of itemId: the deterministic, keyword-carrying awards. A
    // Monster giver becomes `ask <name> <keyword>` at each of its spawn rooms (the
    // full name, which the game always accepts), a Room giver the bare keyword
    // typed verbatim in that room. GiverName is the display name the route card
    // shows ("(ask Gnome Commander)").
    public IReadOnlyList<GiveSource> Free(int itemId) => Resolve(itemId, trades: false);

    // The trades for itemId the character could make right now. Empty while any
    // other certain source exists, so a trade never stands in for a free give, a
    // purchase or a summon.
    public IReadOnlyList<GiveSource> Trades(int itemId) =>
        _soldOrSummoned(itemId) || Free(itemId).Count > 0
            ? Array.Empty<GiveSource>()
            : Resolve(itemId, trades: true);

    // What the give router may act on: the free hand-overs, or failing those the
    // trades when one was agreed to.
    public IReadOnlyList<GiveSource> ForRouter(int itemId, bool tradeAgreed)
    {
        IReadOnlyList<GiveSource> free = Free(itemId);
        return free.Count > 0 || !tradeAgreed ? free : Trades(itemId);
    }

    // The source the give router would detour to on a walk from source to
    // destination, or null when it has none it can reach. Shares the router's
    // selection and its distances, so a route card names the giver the run visits.
    public GiveSource? Choose(int itemId, RoomKey source, RoomKey destination, bool tradeAgreed) =>
        PathItemGiveRouter.TrySelectGiver(
            ForRouter(itemId, tradeAgreed), source, destination, DetourDistance, out GiveSource best)
            ? best
            : null;

    // Distance used to score a detour for a path item. The acquirable gates are
    // suspended: the walk is priced with the fetched item in hand, and the
    // destination of a walk that needs a key is behind the door the key opens. On
    // the live filter that leg has no length and every source is turned down.
    public int? DetourDistance(RoomKey a, RoomKey b)
    {
        using (_filter.SuspendAcquirableGates())
            return _bfs.DistanceBetween(a, b, _filter);
    }

    // How a key nothing else yields is come by, for a route card and the log:
    // "sleazy shopkeeper trades one for opal brooch, which captain of the guard
    // drops", or "... for your opal brooch" with one in the pack. Null for an
    // item with any other certain source, or no trade.
    public string? TradeNote(int itemId)
    {
        if (_soldOrSummoned(itemId) || Free(itemId).Count > 0) return null;
        foreach (ItemGiver g in _index.GiversOf(itemId))
        {
            if (!IsTrade(g)) continue;
            string wanted = _itemName(g.TradeItemId) ?? $"item #{g.TradeItemId}";
            if (_unwornCount(g.TradeItemId) > 0) return $"{g.Name} trades one for your {wanted}";
            return _alwaysDroppedBy(g.TradeItemId) is { Length: > 0 } monster
                ? $"{g.Name} trades one for {wanted}, which {monster} drops"
                : $"{g.Name} trades one for {wanted}";
        }
        return null;
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
