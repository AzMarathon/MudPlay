using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// JSON-friendly gang-house (GH) room label for Roomba Mode. Unlike RoomRef
// (a bare coordinate), this carries the sort rules the room is labeled for —
// a list of GhCategoryRule (OR'd together, so one room can sort for several
// categories at once) plus an optional catch-all flag. Kept as its own type
// rather than extending RoomRef so RoomRef's plain-coordinate contract stays
// unambiguous everywhere else it's used.
public sealed class GhRoomLabel
{
    public int Map { get; set; }
    public int Room { get; set; }

    public List<GhCategoryRule> Rules { get; set; } = new();

    // At most one labeled room in the set may be the catch-all — the room
    // anything matching no explicit rule anywhere gets swept to, instead of
    // being left in place. Enforced single-owner by GhRoomLabelStore.
    public bool IsCatchAll { get; set; }

    public GhRoomLabel() { }

    public GhRoomLabel(int map, int room)
    {
        Map = map;
        Room = room;
    }
}
