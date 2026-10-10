namespace MudPlay.Game.Map;

// Whether a room's own spell (Room.Spell) damages whoever stands in the room, as
// RoomSpellDamageClassifier reads it from the game data. The game re-casts the spell
// every six seconds or so (GAME_MECHANICS "Room-spell monster summons"), so a room
// that damages on every cast breaks a rest again and again. In order of strength.
public enum RoomSpellDamage
{
    // Nothing the spell can reach does damage.
    None,

    // The damage hangs on something about the character or the room that the client
    // doesn't work out: a class, a level, alignment, monsters present, a quest flag.
    // For some characters it never comes.
    Conditional,

    // The damage comes on a roll: a table that mostly does something else, a skill
    // test. A rest there is broken now and then and still recovers.
    OnARoll,

    // The spell damages every time it is cast, unless a counter is held (a raft, a
    // buff, an item that negates the spell). Whether the character has the counter
    // is RoomHazardIndex's to say. The one class that bars resting unless the user
    // says otherwise (Settings → Periodic Damage Room Spells).
    EveryTick,
}
