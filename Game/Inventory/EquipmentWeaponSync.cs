using System;
using System.Linq;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Inventory;

// Derives the combat weapon-swap matrix — the six weapon fields on
// CombatSettings — from the Equipment Manager's gear sets, which are the single
// editing surface for combat weapons (the Combat tab no longer picks them).
// Applied as a read-time overlay onto the CombatSettings that CombatManager
// reads each round, so the values always reflect the current gear sets and the
// backstab fallback is re-evaluated live against the set's Enabled state.
//
// Normal + alternate weapons come from the Default set (its Weapon / OffHand and
// the virtual AlternateWeapon / AlternateOffHand slots). Backstab gear comes
// from the Backstab set when the user has enabled it, otherwise it falls back to
// the Default set's weapon so "Do BS attacks" works without a dedicated backstab
// loadout.
public static class EquipmentWeaponSync
{
    // Overlay the gear-set-derived weapons onto combat. Overwrites all six
    // weapon fields unconditionally (clearing one to null when its gear slot is
    // unset) so removing a gear item also clears the combat reference.
    public static void ApplyWeapons(CombatSettings combat, EquipmentSettings equipment)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(equipment);

        EquipmentSet? def = Find(equipment, EquipTriggerType.Default);
        EquipmentSet? backstab = Find(equipment, EquipTriggerType.Backstab);

        combat.NormalWeapon     = Slot(def, EquipmentSlot.Weapon);
        combat.NormalOffHand    = Slot(def, EquipmentSlot.OffHand);
        combat.AlternateWeapon  = Slot(def, EquipmentSlot.AlternateWeapon);
        combat.AlternateOffHand = Slot(def, EquipmentSlot.AlternateOffHand);

        // Backstab gear: the Backstab set when the user enabled it, otherwise the
        // Default set (so "Do BS attacks" works with just the baseline weapon).
        EquipmentSet? bsSource = backstab is { Enabled: true } ? backstab : def;
        combat.BackstabWeapon  = Slot(bsSource, EquipmentSlot.Weapon);
        combat.BackstabOffHand = Slot(bsSource, EquipmentSlot.OffHand);
    }

    // Write a combat profile's stored weapons back into the Default gear set — the
    // inverse of ApplyWeapons for the four Default-set weapon slots. Switching a
    // combat profile calls this so the incoming profile's weapons become the live
    // loadout that ApplyWeapons + AutoEquipCoordinator already read, with no
    // combat-engine change. Creates the Default set / slot entries when absent; a
    // null profile weapon clears the slot (drops its entry) so a no-weapon profile
    // actually bares the hand. Backstab weapon is untouched — it stays global on
    // the Backstab set.
    public static void WriteProfileWeapons(EquipmentSettings equipment, CombatSpellProfile profile)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(profile);

        EquipmentSet def = FindOrCreate(equipment, EquipTriggerType.Default);
        SetSlot(def, EquipmentSlot.Weapon,           profile.NormalWeapon);
        SetSlot(def, EquipmentSlot.OffHand,          profile.NormalOffHand);
        SetSlot(def, EquipmentSlot.AlternateWeapon,  profile.AlternateWeapon);
        SetSlot(def, EquipmentSlot.AlternateOffHand, profile.AlternateOffHand);
    }

    // Read the Default gear set's four weapon slots back into a profile's stored
    // weapon fields. Used on switch-away so any Workshop weapon edit made while the
    // profile was active (its weapons live in the Default set while active) is
    // remembered before the incoming profile overwrites the set.
    public static void CaptureDefaultWeapons(EquipmentSettings equipment, CombatSpellProfile profile)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(profile);

        EquipmentSet? def = Find(equipment, EquipTriggerType.Default);
        profile.NormalWeapon     = Slot(def, EquipmentSlot.Weapon);
        profile.NormalOffHand    = Slot(def, EquipmentSlot.OffHand);
        profile.AlternateWeapon  = Slot(def, EquipmentSlot.AlternateWeapon);
        profile.AlternateOffHand = Slot(def, EquipmentSlot.AlternateOffHand);
    }

    private static EquipmentSet? Find(EquipmentSettings equipment, EquipTriggerType trigger)
        => equipment.Sets.FirstOrDefault(s => s.Trigger == trigger);

    private static EquipmentSet FindOrCreate(EquipmentSettings equipment, EquipTriggerType trigger)
    {
        EquipmentSet? set = Find(equipment, trigger);
        if (set is null)
        {
            set = new EquipmentSet { Trigger = trigger, Name = trigger.ToString() };
            equipment.Sets.Add(set);
        }
        return set;
    }

    // Upsert (non-empty) or clear (null / empty) a single slot's item on a set,
    // keeping the slot list sparse — an unset weapon slot carries no entry, which
    // ApplyWeapons and a Workshop {no change} row both read as bare.
    private static void SetSlot(EquipmentSet set, EquipmentSlot slot, string? itemName)
    {
        string? trimmed = itemName?.Trim();
        EquipmentSlotEntry? entry = set.Slots.FirstOrDefault(e => e.Slot == slot);
        if (string.IsNullOrEmpty(trimmed))
        {
            if (entry is not null) set.Slots.Remove(entry);
            return;
        }
        if (entry is null) set.Slots.Add(new EquipmentSlotEntry(slot, trimmed));
        else entry.ItemName = trimmed;
    }

    // The trimmed item name a set wants in slot, or null when the set is missing
    // or the slot is unset / blank.
    private static string? Slot(EquipmentSet? set, EquipmentSlot slot)
    {
        if (set is null) return null;
        foreach (EquipmentSlotEntry entry in set.Slots)
            if (entry.Slot == slot)
            {
                string? name = entry.ItemName?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
        return null;
    }
}
