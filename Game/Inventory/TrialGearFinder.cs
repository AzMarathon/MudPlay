using System;
using System.Collections.Generic;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Inventory;

// One "Find best" ranking option for the Item Finder's trial gearset: a label and
// the per-item score it maximizes. The starter set mirrors the equipment fields a
// planner cares about; scores read straight off the pre-projected ItemFinderEntry.
public sealed record TrialFindFilter(string Label, Func<ItemFinderEntry, double> Score);

// Picks the best equippable item per slot for a chosen filter — the engine behind
// the trial-gearset "Find Best" button. Generalizes the single-stat per-slot argmax
// in MaxStrengthIndex to any scoring function, adds slot-Hold locks, and handles the
// paired Finger/Wrist slots (which the catalog collapses to their slot-1 variant) by
// dealing out the top-N DISTINCT items across the pair — the game refuses two
// identically-named worn items.
public static class TrialGearFinder
{
    // The starter filter set (MME-style; adjust as needed). Each scores an
    // ItemFinderEntry field; a non-positive score means "doesn't contribute", so the
    // slot is left untouched rather than filled with a zero-value item.
    public static readonly IReadOnlyList<TrialFindFilter> Filters = new[]
    {
        new TrialFindFilter("Armour Class",     e => e.Ac),
        new TrialFindFilter("AC Blur",          e => e.AcBlur),
        new TrialFindFilter("Damage Resist",    e => e.Dr),
        // Total max-damage contribution: a weapon's base Max plus any item's +Max
        // Damage bonus — so armour / jewellery that carries +damage fills too, not
        // just the weapon slot (base Max is a weapon-only field).
        new TrialFindFilter("Max Damage",       e => e.MaxDmg + e.MaxDamageBonus),
        new TrialFindFilter("Min Damage",       e => e.MinDmg + e.MinDamageBonus),
        new TrialFindFilter("Accuracy",         e => e.Accuracy),
        new TrialFindFilter("Crits",            e => e.Crits),
        new TrialFindFilter("Hit Points",       e => e.Hp),
        new TrialFindFilter("Mana",             e => e.Mana),
        new TrialFindFilter("HP Regen",         e => e.HpRegen),
        new TrialFindFilter("Mana Regen",       e => e.ManaRegen),
        new TrialFindFilter("+Strength",        e => e.Strength),
        new TrialFindFilter("+Intellect",       e => e.Intellect),
        new TrialFindFilter("+Willpower",       e => e.Willpower),
        new TrialFindFilter("+Agility",         e => e.Agility),
        new TrialFindFilter("+Health",          e => e.Health),
        new TrialFindFilter("+Charm",           e => e.Charm),
        new TrialFindFilter("Spell Damage",     e => e.SpellDamage),
        new TrialFindFilter("Cold Resist",      e => e.ColdResist),
        new TrialFindFilter("Fire Resist",      e => e.FireResist),
        new TrialFindFilter("Stone Resist",     e => e.StoneResist),
        new TrialFindFilter("Lightning Resist", e => e.LightningResist),
        new TrialFindFilter("Water Resist",     e => e.WaterResist),
        new TrialFindFilter("Shadow Resist",    e => e.ShadowResist),
    };

    // Best item name per NON-held target slot for the given filter, gated to what the
    // character can equip. Held slots are left out of the result (the caller keeps
    // their current item); a slot whose best candidate scores ≤ 0 is also left out.
    // `current` supplies the present per-slot picks so a held ring/bracelet isn't
    // handed out again to its paired partner.
    public static Dictionary<EquipmentSlot, string> FindBest(
        IReadOnlyList<ItemFinderEntry> catalog,
        IReadOnlyList<EquipmentSlot> targetSlots,
        ISet<EquipmentSlot> heldSlots,
        IReadOnlyDictionary<EquipmentSlot, string?> current,
        Func<ItemFinderEntry, double> score,
        int level, ClassEquipProfile cls, AlignmentBucket? alignment,
        Func<ItemFinderEntry, bool>? extraFilter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(targetSlots);
        ArgumentNullException.ThrowIfNull(score);

        // Positive-scoring, equippable candidates grouped by catalog slot, best first.
        // extraFilter carries the finder's own requirement gates (level / strength req)
        // so Find Best obeys the same left-panel restrictions the results list does.
        var bySlot = new Dictionary<EquipmentSlot, List<(ItemFinderEntry E, double S)>>();
        foreach (ItemFinderEntry e in catalog)
        {
            if (e.IsSynthetic) continue;
            double s = score(e);
            if (s <= 0) continue;
            if (extraFilter is not null && !extraFilter(e)) continue;
            if (!ItemEquipFilter.CanEquip(e.Row, level, cls, alignment)) continue;
            if (!bySlot.TryGetValue(e.Slot, out var list)) bySlot[e.Slot] = list = new();
            list.Add((e, s));
        }
        foreach (var list in bySlot.Values)
            list.Sort((a, b) => b.S.CompareTo(a.S));

        // Names already committed within each paired family (seeded with held picks),
        // so Finger1/Finger2 (and Wrist1/Wrist2) never resolve to the same item.
        var takenByFamily = new Dictionary<EquipmentSlot, HashSet<string>>();
        HashSet<string> Taken(EquipmentSlot family)
        {
            if (!takenByFamily.TryGetValue(family, out var set))
                takenByFamily[family] = set = new(StringComparer.OrdinalIgnoreCase);
            return set;
        }
        foreach (EquipmentSlot t in targetSlots)
            if (heldSlots.Contains(t) && current.TryGetValue(t, out string? held) && !string.IsNullOrWhiteSpace(held))
                Taken(EquipmentSlotMap.PrimarySlot(t)).Add(held!.Trim());

        var result = new Dictionary<EquipmentSlot, string>();
        foreach (EquipmentSlot t in targetSlots)
        {
            if (heldSlots.Contains(t)) continue;
            EquipmentSlot family = EquipmentSlotMap.PrimarySlot(t);
            if (!bySlot.TryGetValue(family, out var list)) continue;
            HashSet<string> taken = Taken(family);
            foreach ((ItemFinderEntry e, double _) in list)
            {
                if (taken.Contains(e.Name)) continue;
                taken.Add(e.Name);
                result[t] = e.Name;
                break;
            }
        }
        return result;
    }
}
