using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

// One per-character record of a player encountered in the world, maintained by
// Game.PlayerSightingTracker and surfaced by the Session Stats → Players Seen
// window. A sighting is recorded when a known player is matched in the local
// "Also here:" listing or walks into the current room; the row aggregates by
// player so TimesSeen is the running total across the character's whole history
// (not just the current session — this list persists on the profile).
//
// Keyed on GIVEN name, the stable identity across family-name (train-stats)
// changes, matching how PlayerDatabase keys its observation rows.
public sealed class PlayerSighting
{
    // The player's given name — the row's identity and the "Who" column.
    public string Name { get; set; } = string.Empty;

    // When this player was most recently seen (local wall-clock). Rendered in the
    // window's Timestamp column in the same ddMMMyy HH:mm:ss format the
    // Transaction-history window uses.
    public DateTimeOffset LastSeen { get; set; }

    // Where the player was last seen — the current room's (Map, Room) and name at
    // sighting time. Map/Room are 0 and RoomName null when the room was unknown.
    public int Map { get; set; }
    public int Room { get; set; }
    public string? RoomName { get; set; }

    // Total number of distinct times this player has been seen — one per room
    // visit they were present for, plus one per walk-in arrival.
    public int TimesSeen { get; set; }

    // "Name (map/room)" for the window's Where column, or "—" when the room was
    // unknown at sighting time. Display-only — not persisted.
    [JsonIgnore]
    public string WhereText =>
        RoomName is null && Map == 0 && Room == 0
            ? "—"
            : $"{RoomName ?? "???"} ({Map}/{Room})";
}
