using System;

namespace MudPlay.Models.Profile;

// One named "combat profile" — a quick-swap snapshot of a whole combat posture,
// not just spells. It captures the Combat tab's six CombatSpellSlots + their
// mana-mode / drain knobs, the physical attack verbs, the room-skip / flee
// thresholds, the primary/alternate WEAPONS (+ off-hands), and the entire Health
// tab (rest / heal / flee thresholds, meditate/shadow, pre-/post-rest commands).
// Switching a profile swaps all of it at once. Targeting / backstab / action-order
// stay shared across profiles on the live CombatSettings.
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

    // Slot-governing fields captured alongside the slots.
    public ThresholdMode SpellManaThresholdMode { get; set; } = ThresholdMode.Percentage;
    public int DrainHpTrigger { get; set; } = 50;
    public bool DrainsOverrideAoe { get; set; }

    // Physical attack verbs + room-skip / flee thresholds — a full combat posture,
    // not just spells.
    public string NormalAttackCommand { get; set; } = "a";
    public string AlternateAttackCommand { get; set; } = "a";
    public int MinMonstersInRoom { get; set; }
    public int MaxMonstersInRoom { get; set; } = 20;
    public int RunDistance { get; set; } = 2;

    // Per-profile primary/alternate weapons + off-hands. The ACTIVE profile's weapons
    // live in the Workshop Default gear set (the live surface combat + the auto-equip
    // coordinator read); these stored fields are the per-profile memory for when the
    // profile is inactive. The manager snapshots them out of / restores them into the
    // Default set on switch (EquipmentWeaponSync). Backstab weapon stays global on the
    // Workshop Backstab set, so it isn't captured here.
    public string? NormalWeapon { get; set; }
    public string? NormalOffHand { get; set; }
    public string? AlternateWeapon { get; set; }
    public string? AlternateOffHand { get; set; }

    // The whole Health tab captured for this profile. Switching a profile swaps the
    // live Settings["Health"] to a clone of this.
    public HealthSettings Health { get; set; } = new();

    // Snapshot the Combat-tab + Health-tab fields into a fresh profile. Weapons are
    // NOT captured here — they live in the gear set, so the caller (the manager)
    // fills them from the Default set via EquipmentWeaponSync.CaptureDefaultWeapons.
    public static CombatSpellProfile Capture(string name, CombatSettings src, HealthSettings health)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(health);
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
            NormalAttackCommand = src.NormalAttackCommand,
            AlternateAttackCommand = src.AlternateAttackCommand,
            MinMonstersInRoom = src.MinMonstersInRoom,
            MaxMonstersInRoom = src.MaxMonstersInRoom,
            RunDistance = src.RunDistance,
            Health = health.Clone(),
        };
    }

    // Overlay this profile's Combat-tab fields (spells + verbs + room thresholds)
    // onto a live CombatSettings, leaving targeting / backstab / action-order
    // untouched. Health is written separately by the manager (writeHealth) and
    // weapons flow to the Default gear set (EquipmentWeaponSync), not here.
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
        dst.NormalAttackCommand = NormalAttackCommand;
        dst.AlternateAttackCommand = AlternateAttackCommand;
        dst.MinMonstersInRoom = MinMonstersInRoom;
        dst.MaxMonstersInRoom = MaxMonstersInRoom;
        dst.RunDistance = RunDistance;
    }

    // Overwrite just the Combat-tab fields (spells + verbs + room thresholds) from a
    // live CombatSettings, leaving Health + weapons + identity + name intact. The
    // Settings-window staging uses this to fold the Combat tab's boxes into a working
    // profile without clobbering what the Health tab or the weapon pickers folded.
    // Exact inverse of ApplyTo.
    public void CaptureCombatFrom(CombatSettings src)
    {
        ArgumentNullException.ThrowIfNull(src);
        MultiAttackSpell = src.MultiAttackSpell.Clone();
        AreaDebuffSpell = src.AreaDebuffSpell.Clone();
        SingleTargetDebuffSpell = src.SingleTargetDebuffSpell.Clone();
        NormalAttackSpell = src.NormalAttackSpell.Clone();
        AlternateAttackSpell = src.AlternateAttackSpell.Clone();
        DrainSpell = src.DrainSpell.Clone();
        SpellManaThresholdMode = src.SpellManaThresholdMode;
        DrainHpTrigger = src.DrainHpTrigger;
        DrainsOverrideAoe = src.DrainsOverrideAoe;
        NormalAttackCommand = src.NormalAttackCommand;
        AlternateAttackCommand = src.AlternateAttackCommand;
        MinMonstersInRoom = src.MinMonstersInRoom;
        MaxMonstersInRoom = src.MaxMonstersInRoom;
        RunDistance = src.RunDistance;
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
        NormalAttackCommand = NormalAttackCommand,
        AlternateAttackCommand = AlternateAttackCommand,
        MinMonstersInRoom = MinMonstersInRoom,
        MaxMonstersInRoom = MaxMonstersInRoom,
        RunDistance = RunDistance,
        NormalWeapon = NormalWeapon,
        NormalOffHand = NormalOffHand,
        AlternateWeapon = AlternateWeapon,
        AlternateOffHand = AlternateOffHand,
        Health = Health.Clone(),
    };
}
