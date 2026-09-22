namespace MudPlay.Models.Profile;

// How a rule's two location criteria combine when BOTH are filled in.
public enum LocationEquipMatchMode
{
    And,
    Or,
}

// A single "wear this item while I'm in this area" rule, edited in Settings →
// Other and persisted at the Character tier. The area is described by up to two
// criteria — a set of map/room numbers and a room-name substring — combined per
// Match. An empty criterion doesn't constrain (a rule can match on either one
// alone, or on both together via the And/Or dropdown). The action is fixed:
// wear ItemName when we're carrying it and we've entered the area; revert the
// slot to whatever the Equipment Manager's current gear set holds on the way
// out. Consumed by Game.Inventory.LocationEquipManager.
public sealed class LocationEquipRule
{
    // Off rules are kept in the list but never fire — a per-row toggle so the
    // user can park a rule without deleting it.
    public bool Enabled { get; set; } = true;

    // Map/room numbers, free text. Accepts "map/room" pairs and bare room
    // numbers, comma- or whitespace-separated (e.g. "16/153, 16/154" or
    // "153 154"). A bare number matches that room in any map; a map/room pair
    // matches only that exact room. Empty = this criterion doesn't constrain.
    public string MapRoomNumbers { get; set; } = "";

    // Combines the two criteria when both are non-empty. Ignored when only one
    // side is filled (that side matches alone).
    public LocationEquipMatchMode Match { get; set; } = LocationEquipMatchMode.Or;

    // Case-insensitive substring the current room's title must contain (e.g.
    // "Black Wastelands"). Empty = this criterion doesn't constrain.
    public string RoomNameContains { get; set; } = "";

    // The item to wear while in the area, exactly as it reads in inventory
    // (e.g. "feathered mask"). Worn only when actually carried.
    public string ItemName { get; set; } = "";
}
