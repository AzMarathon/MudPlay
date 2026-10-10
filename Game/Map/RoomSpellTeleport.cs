namespace MudPlay.Game.Map;

// Whether a room's cast-on-enter spell (Room.Spell) moves whoever it lands on, as
// RoomSpellTeleportClassifier reads it from the game data. The split is the one the
// user asked the map to colour by (GAME_MECHANICS "Room spells that teleport"):
// can you tell beforehand whether it will take you?
public enum RoomSpellTeleport
{
    // Nothing the spell can reach teleports. Green.
    None,

    // Every teleport the spell reaches hangs on conditions alone, with no roll: an
    // item carried or lacked, a class, a level, alignment, monsters present, a quest
    // flag, a buff. It takes you or it doesn't, and which is knowable. Yellow.
    Conditional,

    // Some teleport is not yours to foresee: either nothing gates it at all, or a
    // roll decides it (a `random` table that doesn't always teleport, a `testskill`,
    // an EndCast% under 100), whatever conditions sit beside the roll. Red.
    Sudden,

    // No teleport was found, but part of the spell's chain couldn't be read (a
    // spell or textblock the set lacks, a continuation block, a chain cut for
    // length), so "none" would be a guess. Not painted as any of the three.
    Unknown,
}
