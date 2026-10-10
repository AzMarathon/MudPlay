using System.Collections.Generic;

namespace MudPlay.Game.Map;

// One line of Settings → Periodic Damage Room Spells: a room spell of the loaded
// game data that does damage, what was read of it, what counters it (in words, ""
// when the data has no counter) and the rooms that carry it.
public sealed record PeriodicDamageRoomSpell(
    int Number,
    string Name,
    RoomSpellDamageReading Reading,
    string Counters,
    IReadOnlyList<Room> Rooms);
