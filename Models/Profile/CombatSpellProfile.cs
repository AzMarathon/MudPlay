using System;

namespace MudPlay.Models.Profile;

// One named "casting spell profile" — a quick-swap snapshot of JUST the Combat
// tab's spell configuration: the six CombatSpellSlots plus the mana-threshold
// mode and the drain HP trigger that govern them. Non-spell combat settings
// (attack verbs, targeting, backstab, room thresholds, action order) are NOT part
// of a profile — they stay shared across profiles on the live CombatSettings, so
// switching a profile only swaps which spells fire and their per-slot gates.
//
// Stored per character in the top-level CharacterProfile.CombatProfiles blob
// (like Equipment / PartyBuffs), never a tier-merged Settings section.
public sealed class CombatSpellProfile
{
    // Stable identity — survives rename / reorder, so a placed toolbar button and
    // the active pointer keep referring to the same profile. The user-facing
    // "number" is the 1-based position in the list, not this.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // User label. Empty → shown as "Profile <n>" by position; @profile matches on
    // it by best-match when set.
    public string Name { get; set; } = string.Empty;

    // The captured spell slots (own copies, never shared with live settings).
    public CombatSpellSlot MultiAttackSpell { get; set; } = new();
    public CombatSpellSlot AreaDebuffSpell { get; set; } = new();
    public CombatSpellSlot SingleTargetDebuffSpell { get; set; } = new();
    public CombatSpellSlot NormalAttackSpell { get; set; } = new();
    public CombatSpellSlot AlternateAttackSpell { get; set; } = new();
    public CombatSpellSlot DrainSpell { get; set; } = new();

    // Slot-governing fields captured alongside the slots (per the feature's scope).
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;
    public int DrainHpTrigger { get; set; } = 50;
    public bool DrainsOverrideAoe { get; set; }

    // Snapshot the spell fields off a live CombatSettings into a fresh profile.
    public static CombatSpellProfile Capture(string name, CombatSettings src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return new CombatSpellProfile
        {
            Name = name ?? string.Empty,
            MultiAttackSpell = src.MultiAttackSpell.Clone(),
            AreaDebuffSpell = src.AreaDebuffSpell.Clone(),
            SingleTargetDebuffSpell = src.SingleTargetDebuffSpell.Clone(),
            NormalAttackSpell = src.NormalAttackSpell.Clone(),
            AlternateAttackSpell = src.AlternateAttackSpell.Clone(),
            DrainSpell = src.DrainSpell.Clone(),
            SpellManaThresholdMode = src.SpellManaThresholdMode,
            DrainHpTrigger = src.DrainHpTrigger,
            DrainsOverrideAoe = src.DrainsOverrideAoe,
        };
    }

    // Overlay this profile's spell fields onto a live CombatSettings, leaving every
    // non-spell field (verbs, targeting, backstab, thresholds, action order)
    // untouched. Clones so the live settings never share a slot reference with the
    // stored profile.
    public void ApplyTo(CombatSettings dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        dst.MultiAttackSpell = MultiAttackSpell.Clone();
        dst.AreaDebuffSpell = AreaDebuffSpell.Clone();
        dst.SingleTargetDebuffSpell = SingleTargetDebuffSpell.Clone();
        dst.NormalAttackSpell = NormalAttackSpell.Clone();
        dst.AlternateAttackSpell = AlternateAttackSpell.Clone();
        dst.DrainSpell = DrainSpell.Clone();
        dst.SpellManaThresholdMode = SpellManaThresholdMode;
        dst.DrainHpTrigger = DrainHpTrigger;
        dst.DrainsOverrideAoe = DrainsOverrideAoe;
    }

    // A deep copy of the whole profile (new Id) — backs "add a copy" style flows
    // and defensive snapshots.
    public CombatSpellProfile Clone(bool newIdentity) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        MultiAttackSpell = MultiAttackSpell.Clone(),
        AreaDebuffSpell = AreaDebuffSpell.Clone(),
        SingleTargetDebuffSpell = SingleTargetDebuffSpell.Clone(),
        NormalAttackSpell = NormalAttackSpell.Clone(),
        AlternateAttackSpell = AlternateAttackSpell.Clone(),
        DrainSpell = DrainSpell.Clone(),
        SpellManaThresholdMode = SpellManaThresholdMode,
        DrainHpTrigger = DrainHpTrigger,
        DrainsOverrideAoe = DrainsOverrideAoe,
    };
}
