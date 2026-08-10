namespace MudPlay.Game.Map;

// How the Navigation map paints room-spell rooms (rooms whose Room.Spell > 0). The
// Spells chip cycles through these in order: Mono -> ByName -> Off -> Mono.
// Serialized by name (JsonStringEnumConverter) when persisted per-character, so the
// declaration order is free to change without breaking saved profiles.
public enum SpellDisplayMode
{
    // Every room-spell room drawn in the single flat purple spell colour — the "is
    // there a room spell here?" cue, with no distinction between different spells.
    Mono,

    // Each room-spell room coloured by WHICH spell it carries: the spell record
    // number is hashed into a categorical palette so differing room spells clustered
    // together (e.g. Swamp of Tharollok's monster-spawners vs the swamp-poison) read
    // as different colours. Hover a room to see its spell name in the tooltip — the
    // colour has no legend, the tooltip is the key. Rare spells may share a colour
    // (there are far more spells than palette slots), which the tooltip disambiguates.
    ByName,

    // Room-spell rooms get no special fill; they render like any other room.
    Off,
}
