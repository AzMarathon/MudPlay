namespace MudPlay.Models.Profile;

// Per-character "Periodic Damage Room Spells" settings. Stored as the
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
    // the loaded game data doesn't list is kept as it is.
    public Dictionary<int, bool> BarsResting { get; set; } = new();
}
