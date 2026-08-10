namespace MudPlay.Game.Map;

// How the Navigation map paints lair rooms. The Lairs chip cycles through
// these in order: Uniform -> Heat -> Count -> Heat+Count -> Off -> Uniform.
// Serialized by name (JsonStringEnumConverter) when persisted per-character, so
// the declaration order is free to change without breaking saved profiles.
public enum LairDisplayMode
{
    // Every lair drawn in the single flat lair colour.
    Uniform,

    // Lairs shaded by respawn time — fast (short delay) rooms run hot (red),
    // slow (long delay) rooms run cold (blue). Rooms whose respawn time can't
    // be resolved fall back to the Uniform colour.
    Heat,

    // Lairs drawn in the flat lair colour, each labelled with the max number of
    // monsters it spawns (the "(Max N)" on the room's lair tag). Rooms with no
    // parsed max show no number.
    Count,

    // Both at once: respawn-heat fill AND the max-monster count label, so a
    // glance reads how fast a lair repops and how many it holds.
    HeatCount,

    // Lairs get no special fill; they render like any other room.
    Off,
}
