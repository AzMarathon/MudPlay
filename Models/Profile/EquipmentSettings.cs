namespace MudPlay.Models.Profile;

// Per-character equipment-manager state — the trigger-purposed gear sets. One
// EquipmentSet per EquipTriggerType (the Equipment Manager seeds any that are
// missing). Persisted as the top-level CharacterProfile.Equipment blob (like
// CharacterProfile.CharacterPlan), not a tier-merged Settings section, since it
// is whole-character state rather than a per-tier delta.
public sealed class EquipmentSettings
{
    // The trigger-purposed gear sets, one per EquipTriggerType.
    public System.Collections.Generic.List<EquipmentSet> Sets { get; set; } = new();

    // When true, a fight that interrupts a rest is fought in the Default combat
    // loadout: the coordinator swaps to Default on combat entry and swaps back to
    // the pre-rest set on room-clear if the rest still isn't satisfied. Default
    // false = the long-standing behavior (keep the pre-rest loadout through the
    // fight, revert to Default only once recovered) — surfaced in the Equipment
    // Manager as the "Don't swap to default upon entering combat" checkbox (checked
    // = false here).
    public bool SwapToDefaultOnCombat { get; set; }

    // While the "While Moving" set is in use and this is true, the coordinator
    // swaps to Default the step BEFORE entering a known lair room, so you arrive
    // already in combat gear. Default false = enter the lair in the movement set
    // and swap to Default when hostiles are recognized. Surfaced in the Equipment
    // Manager as the "Swap to default before entering lairs" checkbox, shown only
    // when the movement set is selected.
    public bool SwapToDefaultBeforeLairs { get; set; }

    // Opt-in: also wear the While Moving set when moving BY HAND (typed moves with no
    // loop / Auto-Lair / walk-to running), not only when a nav engine travels. A typed
    // move has no "arrived" moment, so the set comes off once no typed move has gone
    // out for WhileMovingManualIdleSeconds. Surfaced beside the lair option in the
    // Equipment Manager, shown only when the movement set is selected.
    public bool WhileMovingOnManualMoves { get; set; }

    // Seconds without a typed move before a hand-moving While Moving set reverts to
    // Default. Clamped to 1+ where it's read.
    public int WhileMovingManualIdleSeconds { get; set; } = 10;
}
