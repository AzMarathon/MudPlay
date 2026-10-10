using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// How many of each item a set of rooms holds, kept so one copy more or less costs
// the same however much a room holds. A sort touches the count of one item in one
// room for every pickup and delivery it confirms; kept as a list per room, a vault's
// worth of deliveries to one room searched that list once per stack, each longer
// than the last.
internal sealed class GhFloorCounts
{
    private readonly ItemNameStore _itemNames;
    private readonly Dictionary<RoomKey, Dictionary<string, int>> _byRoom = new();

    public GhFloorCounts(ItemNameStore itemNames) => _itemNames = itemNames;

    // The floors as a ledger of "N name" entries holds them.
    public GhFloorCounts(ItemNameStore itemNames, IReadOnlyDictionary<RoomKey, List<string>> ledger)
        : this(itemNames)
    {
        foreach ((RoomKey room, List<string> entries) in ledger)
            foreach (string entry in entries)
                Add(room, entry, CountedCommand.SplitLeadingCount(entry).Count);
    }

    // Puts copies of an item in a room, or takes them out with a negative count.
    public void Add(RoomKey room, string itemName, int copies)
    {
        if (!_byRoom.TryGetValue(room, out Dictionary<string, int>? counts))
            _byRoom[room] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string canonical = GhSurveyMerger.Canonical(itemName, _itemNames);
        counts[canonical] = counts.GetValueOrDefault(canonical) + copies;
    }

    public int Of(RoomKey room, string itemName) =>
        _byRoom.TryGetValue(room, out Dictionary<string, int>? counts)
            ? counts.GetValueOrDefault(GhSurveyMerger.Canonical(itemName, _itemNames))
            : 0;

    // A room's floor as ledger entries, items it holds none of left out.
    public List<string> EntriesAt(RoomKey room) =>
        _byRoom.TryGetValue(room, out Dictionary<string, int>? counts)
            ? counts.Where(c => c.Value > 0).Select(c => c.Value > 1 ? $"{c.Value} {c.Key}" : c.Key).ToList()
            : new List<string>();
}
