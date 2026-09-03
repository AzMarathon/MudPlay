using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// A protectable room-entry hazard on a route step's room (from RoomHazardIndex):
// the harmful cast-on-enter spell, plus the item(s) that make the room safe to
// cross — a raft / log raft / canoe, rope & grapple, phoenix feather, waterskin,
// and so on. Both the spell and each counter item open their Game Data record on
// click.
public sealed class RouteRoomHazard
{
    // The room's cast-on-enter hazard spell → opens its spell record.
    public RoomDetailLink Spell { get; }

    // The items that make the room safe (any one is enough) → each opens its item
    // record. Empty only for a hazard the data ships no counter for (which the
    // index doesn't hold anyway, so in practice always populated).
    public IReadOnlyList<RoomDetailLink> Counters { get; }

    public bool HasCounters => Counters.Count > 0;

    public RouteRoomHazard(RoomDetailLink spell, IReadOnlyList<RoomDetailLink> counters)
    {
        Spell = spell;
        Counters = counters ?? Array.Empty<RoomDetailLink>();
    }
}
