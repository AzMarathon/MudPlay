namespace MudPlay.Game.Map;

// Whether a room's cast-on-enter spell (Room.Spell) moves whoever it lands on, as
// RoomSpellTeleportClassifier reads it from the game data. Ordered weakest to
// strongest so two branches of a spell combine with a plain max / min.
public enum RoomSpellTeleport
{
    // Nothing the spell can reach teleports.
    None,

    // Every teleport the spell can reach sits behind a roll: a `random` table with
    // a band that doesn't teleport, a `testskill`, or an EndCast% under 100.
    Chance,

    // Some branch teleports with no roll in the way. A branch may still be
    // conditional on the character or the room (an item carried, a class, a level,
    // alignment, monsters present): those are facts, not luck, and the map colours
    // a spell the same for every character.
    Always,
}
