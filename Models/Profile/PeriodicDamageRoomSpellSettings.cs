namespace MudPlay.Models.Profile;

// Per-character "Room Spell Config" settings. Stored as the
// "PeriodicDamageRoomSpells" entry in CharacterProfile.Settings, the tier the Health
// tab's rest settings live on: HealthManager's rest rule reads both for the loaded
// character.
public sealed class PeriodicDamageRoomSpellSettings
{
    public const string TabKey = "PeriodicDamageRoomSpells";

    // Room spell number → whether a room with that spell bars resting, for the
    // spells the user set away from the default only (RoomSpellDamageIndex
    // .BarsRestingByDefault: barred where the damage comes every tick). A spell that
    // isn't listed follows the default, so a game-data update that reads a spell
    // differently carries over to every character that never touched it. A number
    // the loaded game data doesn't list is kept as it is, and does nothing: a spell
    // the list leaves out (RoomSpellDamageIndex.LeftOffBecause) never bars resting,
    // whatever was stored for it (RoomSpellDamageIndex.BarsResting).
    public Dictionary<int, bool> BarsResting { get; set; } = new();

    // Put on a carried item that negates a room's spell before stepping into the
    // room (the phoenix feather ahead of magma heat), and give its slot back to
    // the gear sets once no such room is near (RoomSpellCounterWear). On by
    // default: such an item does nothing from the pack. Off, a route is not
    // planned on a negating item that isn't already worn.
    public bool WearCounterBeforeEntering { get; set; } = true;
}
