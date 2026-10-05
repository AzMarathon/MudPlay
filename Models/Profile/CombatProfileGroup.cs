namespace MudPlay.Models.Profile;

// A settings group (one header in Settings) that a combat profile can carry. Each
// can be marked shared instead (CombatProfileSettings.SharedGroups): one value for
// the whole character, the same under every combat profile. Stored by name, so
// don't rename members.
public enum CombatProfileGroup
{
    // Combat tab
    ActionOrder,
    WeaponsAndCommands,
    Backstab,
    RoomThresholds,
    SpellCombat,
    // Health tab
    HealthHp,
    HealthMana,
    RestingOptions,
    EmergencyEscape,
    RestingCommands,
    // Spells tab
    SpellPriority,
    HealingRegen,
    // LEGACY: bless timing moved onto each buff slot; kept so stored group sets still load.
    BlessTiming,
    // Party tab
    PartyHealing,
    PartyBless,
}
