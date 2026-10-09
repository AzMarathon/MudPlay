using MudPlay.Game.Map;

namespace MudPlay.Services;

// What picking a route card's gated route does about each gate item on it, worked
// out once so the card's wording and the walk it starts can't disagree. An item
// the pick fetches names where from; an item it leaves alone names no source it
// isn't going to use; and a key it fetches by trade is listed, so the pick agrees
// to exactly the trades the card showed.
public sealed class GatePickSources
{
    private readonly Dictionary<int, string> _givers = new();
    private readonly Dictionary<int, string> _notes = new();
    private readonly Dictionary<int, string> _droppers = new();
    private readonly List<int> _trades = new();

    private GatePickSources() { }

    // The door keys this pick fetches by handing an item over.
    public IReadOnlyList<int> Trades => _trades;

    // The three tails a requirement clause can carry; at most one is set per item.
    public string? GiverName(int itemId) => _givers.GetValueOrDefault(itemId);
    public string? BuyOrTradeNote(int itemId) => _notes.GetValueOrDefault(itemId);
    public string? DropperName(int itemId) => _droppers.GetValueOrDefault(itemId);

    // pickFetches is false for a pick that walks somewhere and stops (to a hazard's
    // edge, to a shop the leader can't pay at): it arranges nothing, so its card
    // names no source.
    //
    // A gate item is sourced by this pick when the pick force-obtains it
    // (RouteChoicePlanner.SourceableGateItems), whatever its Auto-obtain flag says.
    // A hazard counter the picker didn't resolve keeps the flag as its rule.
    //
    // The source helpers answer for the walk this pick would start, ignoring the
    // flag: giver(id, tradeAgreed), buyPhrase(id), dropper(id). tradeNote(id) says
    // how a key is come by when nothing fetches it. Trades are for door keys only:
    // other gate items are quest pieces the walk never goes after on its own.
    public static GatePickSources Build(
        IReadOnlyList<RouteRequirement> requirements,
        bool pickFetches,
        Func<int, bool> keyFetchableIfPicked,
        Func<int, bool> flaggedAutoObtain,
        Func<int, bool, GiveSource?> giver,
        Func<int, string?> buyPhrase,
        Func<int, string?> dropper,
        Func<int, string?> tradeNote)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(keyFetchableIfPicked);
        ArgumentNullException.ThrowIfNull(flaggedAutoObtain);
        ArgumentNullException.ThrowIfNull(giver);
        ArgumentNullException.ThrowIfNull(buyPhrase);
        ArgumentNullException.ThrowIfNull(dropper);
        ArgumentNullException.ThrowIfNull(tradeNote);

        var pick = new GatePickSources();
        IReadOnlyList<int> fetched = pickFetches
            ? RouteChoicePlanner.SourceableGateItems(requirements, keyFetchableIfPicked)
            : Array.Empty<int>();
        var seen = new HashSet<int>();

        foreach (RouteRequirement req in requirements)
        {
            if (req.Carried || req.ItemIds.Count != 1) continue;
            int id = req.ItemIds[0];
            if (id <= 0 || !seen.Add(id)) continue;

            bool isKey = req.Kind == RouteRequirementKind.DoorKey;
            bool sourced = req.Kind == RouteRequirementKind.HazardProtection
                ? flaggedAutoObtain(id)
                : fetched.Contains(id);

            if (sourced)
            {
                if (giver(id, isKey) is { } source)
                {
                    pick._givers[id] = source.GiverName;
                    if (source.TakesItemId > 0) pick._trades.Add(id);
                    continue;
                }
                if (buyPhrase(id) is { Length: > 0 } buy) { pick._notes[id] = buy; continue; }
                if (dropper(id) is { Length: > 0 } monster) { pick._droppers[id] = monster; continue; }
            }
            if (isKey && tradeNote(id) is { Length: > 0 } note) pick._notes[id] = note;
        }
        return pick;
    }
}
