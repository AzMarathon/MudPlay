using System;
using System.Collections.Generic;
using MudPlay.Game.Calculators;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Inventory;

// One "Find best" ranking option for the Item Finder's trial gearset: a label and
// the per-item score it maximizes. The starter set mirrors the equipment fields a
// planner cares about; scores read straight off the pre-projected ItemFinderEntry.
// BackstabRange marks the computed-backstab min / max criteria: which end of the
// resolved range they maximize (see TrialGearFinder.FindBestOfPasses).
public sealed record TrialFindFilter(
    string Label, Func<ItemFinderEntry, double> Score, Func<BSDamageResult, double>? BackstabRange = null);

// Picks the best equippable item per slot for a chosen filter — the engine behind
// the trial-gearset "Find Best" button. Generalizes the single-stat per-slot argmax
// in MaxStrengthIndex to any scoring function, adds slot-Hold locks, and handles the
// paired Finger/Wrist slots (which the catalog collapses to their slot-1 variant) by
// dealing out DISTINCT items across the pair — the game refuses two identically-named
// worn items. Under a weight budget the slots are chosen together, as the best set
// that fits, rather than one slot at a time.
public static class TrialGearFinder
{
    // A positive-scoring, equippable catalog item. Order is its catalog position, the
    // last tie-break, so equal candidates always resolve the same way.
    private readonly record struct Candidate(ItemFinderEntry Item, double Score, int Order)
    {
        // The budget table is indexed by weight, so a weight below zero can't be
        // represented; no game-data item has one.
        public int Weight => Math.Max(0, Item.Encum);
    }

    // One way to fill a slot family under a budget: a single item, or for the paired
    // Finger / Wrist slots two items with different names.
    private readonly record struct Option(int Weight, double Score, Candidate First, Candidate? Second);

    // Every worn-stat ItemFinderEntry field a criterion could reasonably score on —
    // full parity with (and beyond) the MegaMUD reference client's own "Find Best"
    // nested-menu criterion list. A non-positive score means "doesn't contribute",
    // so the slot is left untouched rather than filled with a zero-value item.
    public static readonly IReadOnlyList<TrialFindFilter> Filters = new[]
    {
        new TrialFindFilter("Armour Class",     e => e.Ac),
        // Prot-Evil is a CONFIRMED 1 AC/point vs evil monsters (the majority of
        // monsters) — see GAME_MECHANICS.md. Plain "Armour Class" above stays raw-AC
        // only so a witchwood-bracelet-style item (low Ac, high ProtEvil) isn't
        // penalized for a stat that's genuinely AC almost all the time.
        new TrialFindFilter("Effective AC vs Evil", e => e.Ac + e.ProtEvil),
        new TrialFindFilter("AC Blur",          e => e.AcBlur),
        new TrialFindFilter("AC/DR Combo",      e => e.Ac + e.Dr),
        new TrialFindFilter("Damage Resist",    e => e.Dr),
        new TrialFindFilter("Dodge",            e => e.Dodge),
        new TrialFindFilter("Magic Resist",     e => e.MagicResist),
        new TrialFindFilter("ShockShield",      e => e.ShockShield),
        // Total max-damage contribution: a weapon's base Max plus any item's +Max
        // Damage bonus — so armour / jewellery that carries +damage fills too, not
        // just the weapon slot (base Max is a weapon-only field).
        new TrialFindFilter("Max Damage",       e => e.MaxDmg + e.MaxDamageBonus),
        new TrialFindFilter("Min Damage",       e => e.MinDmg + e.MinDamageBonus),
        new TrialFindFilter("Accuracy",         e => e.Accuracy),
        new TrialFindFilter("Crits",            e => e.Crits),
        new TrialFindFilter("BS Accuracy",      e => e.BsAccuracy),
        new TrialFindFilter("BS Min Damage",    e => e.BsMin),
        new TrialFindFilter("BS Max Damage",    e => e.BsMax),
        // Computed backstab damage for the live character (ItemDamageModel): a
        // weapon's own backstab range, other gear by what it adds to it, so stealth,
        // strength and +max damage count as well as the +BS min / max bonuses above.
        new TrialFindFilter("Backstab Dmg (min)", e => e.BsScoreMin, r => r.MinDamage),
        new TrialFindFilter("Backstab Dmg (max)", e => e.BsScoreMax, r => r.MaxDamage),
        new TrialFindFilter("Backstab Dmg (avg)", e => e.BsScoreAvg),
        // Damage per round for whichever attack type the finder is set to.
        new TrialFindFilter("Damage / Round (attack type)", e => e.DamagePerRoundScore),
        new TrialFindFilter("Punch Accuracy",   e => e.PunchAccy),
        new TrialFindFilter("Punch Damage",     e => e.PunchDmg),
        new TrialFindFilter("Kick Accuracy",    e => e.KickAccy),
        new TrialFindFilter("Kick Damage",      e => e.KickDmg),
        new TrialFindFilter("JumpKick Accuracy", e => e.JumpKickAccy),
        new TrialFindFilter("JumpKick Damage",  e => e.JumpKickDmg),
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
        new TrialFindFilter("+Encumbrance",     e => e.EncumBonus),
        new TrialFindFilter("Illumination",     e => e.Illuminate),
        new TrialFindFilter("Stealth",          e => e.Stealth),
        new TrialFindFilter("Spellcasting",     e => e.Spellcasting),
        new TrialFindFilter("Quickness",        e => e.Quickness),
        new TrialFindFilter("Traps",            e => e.Traps),
        new TrialFindFilter("Picklocks",        e => e.Picklocks),
        new TrialFindFilter("Thievery",         e => e.Thievery),
        new TrialFindFilter("Prot. from Evil",  e => e.ProtEvil),
        new TrialFindFilter("Prot. from Good",  e => e.ProtGood),
        new TrialFindFilter("VileWard",         e => e.VileWard),
        new TrialFindFilter("Cold Resist",      e => e.ColdResist),
        new TrialFindFilter("Fire Resist",      e => e.FireResist),
        new TrialFindFilter("Stone Resist",     e => e.StoneResist),
        new TrialFindFilter("Lightning Resist", e => e.LightningResist),
        new TrialFindFilter("Water Resist",     e => e.WaterResist),
        new TrialFindFilter("Shadow Resist",    e => e.ShadowResist),
    };

    // Best item name per NON-held target slot for the given filter, gated to what the
    // character can equip. Held slots are left out of the result (the caller keeps
    // their current item); a slot with no positive-scoring candidate is also left out.
    // `current` supplies the present per-slot picks so a held ring/bracelet isn't
    // handed out again to its paired partner.
    //
    // Without a `weightBudget` every slot takes its own best item, the paired slots the
    // top two distinct ones. With one, the total Encum of the picks may not exceed it,
    // and the slots compete for it:
    //   - The weapon is settled first: the highest-scoring one that fits, its weight
    //     taken off the budget before anything else is weighed. A weapon's score isn't
    //     on the scale other gear's is (for the computed backstab criteria a weapon
    //     scores its whole resolved range, other gear only what it adds), and it is the
    //     single biggest lever, so it is never traded off against armour.
    //   - The other slots then get the set with the highest total score that fits in
    //     what is left, at most one item per slot, chosen together rather than slot by
    //     slot — so a heavy piece in one slot can't starve several better pieces
    //     elsewhere. Equal totals go to the lighter set. A slot that set leaves empty
    //     is left out of the result.
    public static Dictionary<EquipmentSlot, string> FindBest(
        IReadOnlyList<ItemFinderEntry> catalog,
        IReadOnlyList<EquipmentSlot> targetSlots,
        ISet<EquipmentSlot> heldSlots,
        IReadOnlyDictionary<EquipmentSlot, string?> current,
        Func<ItemFinderEntry, double> score,
        int level, ClassEquipProfile cls, AlignmentBucket? alignment,
        Func<ItemFinderEntry, bool>? extraFilter = null,
        int? weightBudget = null,
        RealmType realm = RealmType.ParaMud,
        EvilPointRange? evilPoints = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(targetSlots);
        ArgumentNullException.ThrowIfNull(score);

        // Positive-scoring, equippable candidates grouped by catalog slot.
        // extraFilter carries the finder's own requirement gates (level / strength req)
        // so Find Best obeys the same left-panel restrictions the results list does.
        var bySlot = new Dictionary<EquipmentSlot, List<Candidate>>();
        for (int i = 0; i < catalog.Count; i++)
        {
            ItemFinderEntry e = catalog[i];
            if (e.IsSynthetic) continue;
            double s = score(e);
            if (s <= 0) continue;
            if (extraFilter is not null && !extraFilter(e)) continue;
            if (!ItemEquipFilter.CanEquip(e.Row, level, cls, alignment, realm, evilPoints)) continue;
            if (!bySlot.TryGetValue(e.Slot, out var list)) bySlot[e.Slot] = list = new();
            list.Add(new Candidate(e, s, i));
        }

        // Names already committed within each paired family (seeded with held picks),
        // so Finger1/Finger2 (and Wrist1/Wrist2) never resolve to the same item.
        var takenByFamily = new Dictionary<EquipmentSlot, HashSet<string>>();
        foreach (EquipmentSlot t in targetSlots)
            if (heldSlots.Contains(t) && current.TryGetValue(t, out string? held) && !string.IsNullOrWhiteSpace(held))
                Taken(takenByFamily, EquipmentSlotMap.PrimarySlot(t)).Add(held!.Trim());

        return weightBudget is int budget
            ? BestSetWithinBudget(bySlot, targetSlots, heldSlots, takenByFamily, budget)
            : BestPerSlot(bySlot, targetSlots, heldSlots, takenByFamily);
    }

    private static HashSet<string> Taken(Dictionary<EquipmentSlot, HashSet<string>> takenByFamily, EquipmentSlot family)
    {
        if (!takenByFamily.TryGetValue(family, out var set))
            takenByFamily[family] = set = new(StringComparer.OrdinalIgnoreCase);
        return set;
    }

    // No budget: nothing ties the slots together, so each takes its own best item.
    private static Dictionary<EquipmentSlot, string> BestPerSlot(
        Dictionary<EquipmentSlot, List<Candidate>> bySlot,
        IReadOnlyList<EquipmentSlot> targetSlots, ISet<EquipmentSlot> heldSlots,
        Dictionary<EquipmentSlot, HashSet<string>> takenByFamily)
    {
        foreach (var list in bySlot.Values)
            list.Sort((a, b) => b.Score.CompareTo(a.Score));

        var result = new Dictionary<EquipmentSlot, string>();
        foreach (EquipmentSlot t in targetSlots)
        {
            if (heldSlots.Contains(t)) continue;
            EquipmentSlot family = EquipmentSlotMap.PrimarySlot(t);
            if (!bySlot.TryGetValue(family, out var list)) continue;
            HashSet<string> taken = Taken(takenByFamily, family);
            foreach (Candidate c in list)
            {
                if (!taken.Add(c.Item.Name)) continue;
                result[t] = c.Item.Name;
                break;
            }
        }
        return result;
    }

    private static Dictionary<EquipmentSlot, string> BestSetWithinBudget(
        Dictionary<EquipmentSlot, List<Candidate>> bySlot,
        IReadOnlyList<EquipmentSlot> targetSlots, ISet<EquipmentSlot> heldSlots,
        Dictionary<EquipmentSlot, HashSet<string>> takenByFamily, int budget)
    {
        var result = new Dictionary<EquipmentSlot, string>();
        int remaining = budget;

        // Weapon first — FindBest's header says why it isn't weighed against the rest.
        foreach (EquipmentSlot t in targetSlots)
        {
            if (!IsWeaponSlot(t) || heldSlots.Contains(t)) continue;
            EquipmentSlot family = EquipmentSlotMap.PrimarySlot(t);
            if (!bySlot.TryGetValue(family, out var list)) continue;
            HashSet<string> taken = Taken(takenByFamily, family);
            Candidate? pick = null;
            foreach (Candidate c in list)
                if (c.Weight <= remaining && !taken.Contains(c.Item.Name) && (pick is not { } p || Outranks(c, p)))
                    pick = c;
            if (pick is not { } weapon) continue;
            taken.Add(weapon.Item.Name);
            result[t] = weapon.Item.Name;
            remaining -= weapon.Weight;
        }

        // The free slots of every other family, in target order: one slot, or both
        // halves of a Finger / Wrist pair.
        var freeSlots = new Dictionary<EquipmentSlot, List<EquipmentSlot>>();
        var families = new List<EquipmentSlot>();
        foreach (EquipmentSlot t in targetSlots)
        {
            if (IsWeaponSlot(t) || heldSlots.Contains(t)) continue;
            EquipmentSlot family = EquipmentSlotMap.PrimarySlot(t);
            if (!freeSlots.TryGetValue(family, out var slots))
            {
                freeSlots[family] = slots = new();
                families.Add(family);
            }
            if (!slots.Contains(t)) slots.Add(t);
        }

        var groups = new List<(List<EquipmentSlot> Slots, List<Option> Options)>();
        long heaviestSet = 0;
        foreach (EquipmentSlot family in families)
        {
            if (!bySlot.TryGetValue(family, out var list)) continue;
            List<Option> options = BuildOptions(list, Taken(takenByFamily, family), freeSlots[family].Count > 1, remaining);
            if (options.Count == 0) continue;
            groups.Add((freeSlots[family], options));
            heaviestSet += options[^1].Weight;
        }
        if (groups.Count == 0) return result;

        // best[w] is the highest total score the families handled so far reach within
        // weight w; choice[g][w] is the option family g contributes to it (-1 for none).
        // No set can weigh more than every family's heaviest option together, so the
        // table stops there even under a far larger budget.
        int capacity = (int)Math.Min(remaining, heaviestSet);
        var best = new double[capacity + 1];
        var choice = new int[groups.Count][];
        for (int g = 0; g < groups.Count; g++)
        {
            List<Option> options = groups[g].Options;
            var next = (double[])best.Clone();
            var chosen = new int[capacity + 1];
            Array.Fill(chosen, -1);
            for (int o = 0; o < options.Count; o++)
            {
                Option option = options[o];
                for (int w = option.Weight; w <= capacity; w++)
                {
                    // Strictly better only, so a tie stays with the lighter option, and
                    // with an earlier slot over a later one.
                    double total = best[w - option.Weight] + option.Score;
                    if (total > next[w])
                    {
                        next[w] = total;
                        chosen[w] = o;
                    }
                }
            }
            best = next;
            choice[g] = chosen;
        }

        // best[] never falls as the weight grows, so the lightest set reaching the top
        // score sits at the first weight where that score appears.
        int weight = capacity;
        while (weight > 0 && best[weight - 1] >= best[capacity]) weight--;

        for (int g = groups.Count - 1; g >= 0; g--)
        {
            int o = choice[g][weight];
            if (o < 0) continue;
            (List<EquipmentSlot> slots, List<Option> options) = groups[g];
            Option option = options[o];
            result[slots[0]] = option.First.Item.Name;
            if (option.Second is { } second) result[slots[1]] = second.Item.Name;
            weight -= option.Weight;
        }
        return result;
    }

    // Weapons are held rather than worn, and under a budget are settled ahead of the
    // worn slots.
    private static bool IsWeaponSlot(EquipmentSlot slot) =>
        slot is EquipmentSlot.Weapon or EquipmentSlot.AlternateWeapon;

    private static bool Outranks(Candidate a, Candidate b)
    {
        if (a.Score != b.Score) return a.Score > b.Score;
        if (a.Weight != b.Weight) return a.Weight < b.Weight;
        return a.Order < b.Order;
    }

    private static bool SameName(Candidate a, Candidate b) =>
        string.Equals(a.Item.Name, b.Item.Name, StringComparison.OrdinalIgnoreCase);

    // The ways one family can spend weight, lightest first, each scoring more than the
    // one before it. Everything else is dropped up front: the budget table costs
    // (weight × options) per family, and a slot can have hundreds of candidates of
    // which only a handful are ever worth their weight.
    private static List<Option> BuildOptions(List<Candidate> candidates, HashSet<string> taken, bool pair, int limit)
    {
        var fits = new List<Candidate>();
        foreach (Candidate c in candidates)
            if (c.Weight <= limit && !taken.Contains(c.Item.Name))
                fits.Add(c);
        fits.Sort(static (a, b) =>
        {
            int byWeight = a.Weight.CompareTo(b.Weight);
            if (byWeight != 0) return byWeight;
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Order.CompareTo(b.Order);
        });

        // An item is beaten by one that weighs no more and scores at least as much:
        // any set using it is no worse with the other in its place. Sorted as above,
        // anything that can beat an item comes before it.
        var kept = new List<Candidate>();
        foreach (Candidate c in fits)
            if (!IsBeaten(c, kept, pair))
                kept.Add(c);

        var options = new List<Option>();
        foreach (Candidate c in kept)
            options.Add(new Option(c.Weight, c.Score, c, null));
        if (pair)
        {
            for (int i = 0; i < kept.Count; i++)
            {
                for (int j = i + 1; j < kept.Count; j++)
                {
                    Candidate a = kept[i], b = kept[j];
                    if (SameName(a, b) || a.Weight + b.Weight > limit) continue;
                    // The stronger item goes to the first slot, as it does with no budget.
                    (Candidate first, Candidate second) = Outranks(b, a) ? (b, a) : (a, b);
                    options.Add(new Option(a.Weight + b.Weight, a.Score + b.Score, first, second));
                }
            }
        }

        options.Sort(static (a, b) =>
        {
            int byWeight = a.Weight.CompareTo(b.Weight);
            if (byWeight != 0) return byWeight;
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byFirst = a.First.Order.CompareTo(b.First.Order);
            return byFirst != 0 ? byFirst : (a.Second?.Order ?? -1).CompareTo(b.Second?.Order ?? -1);
        });
        var worthTheirWeight = new List<Option>();
        double topScore = double.NegativeInfinity;
        foreach (Option option in options)
        {
            if (option.Score <= topScore) continue;
            worthTheirWeight.Add(option);
            topScore = option.Score;
        }
        return worthTheirWeight;
    }

    // A single slot drops an item as soon as one other beats it. A pair fills two
    // slots, so the item that beats this one may already be in the other slot: it takes
    // two beating items to drop one — two with different names, since the pair can't
    // wear the same name twice. One beating item of the SAME name is enough on its own,
    // because it can never be the partner.
    private static bool IsBeaten(Candidate c, List<Candidate> kept, bool pair)
    {
        Candidate? other = null;
        foreach (Candidate k in kept)
        {
            if (k.Score < c.Score) continue;
            if (!pair || SameName(k, c)) return true;
            if (other is not { } o) other = k;
            else if (!SameName(o, k)) return true;
        }
        return false;
    }

    // One entry of a search order: fill what it can of the given free slots within the
    // budget, seeing the slots already settled (held or picked) and what is in them.
    public delegate Dictionary<EquipmentSlot, string> OrderedCriterion(
        IReadOnlyList<EquipmentSlot> freeSlots, ISet<EquipmentSlot> settled,
        IReadOnlyDictionary<EquipmentSlot, string?> current, int? weightBudget);

    // Runs a search order: each criterion in turn gets the slots the ones before it
    // left empty, and what it picks comes off the weight budget for the rest.
    //
    // The weapon is settled before any of that, by the first criterion in the order
    // that finds one. Left to its turn, a criterion ahead of it that scores no weapon
    // (Stealth, then a backstab criterion) would spend the budget on armour first and
    // the weapon would get what was left. Without a budget the weapon comes out the
    // same either way.
    public static Dictionary<EquipmentSlot, string> FindBestInOrder(
        IReadOnlyList<OrderedCriterion> criteria,
        IReadOnlyList<EquipmentSlot> targetSlots,
        ISet<EquipmentSlot> heldSlots,
        IReadOnlyDictionary<EquipmentSlot, string?> current,
        int? weightBudget,
        Func<string, int> weightOf)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(targetSlots);
        ArgumentNullException.ThrowIfNull(heldSlots);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(weightOf);

        var result = new Dictionary<EquipmentSlot, string>();
        var settled = new HashSet<EquipmentSlot>(heldSlots);
        var now = new Dictionary<EquipmentSlot, string?>(current);
        int? budget = weightBudget;

        void Take(Dictionary<EquipmentSlot, string> picks)
        {
            foreach ((EquipmentSlot slot, string name) in picks)
            {
                result[slot] = name;
                settled.Add(slot);
                now[slot] = name;
                if (budget is int left) budget = Math.Max(0, left - Math.Max(0, weightOf(name)));
            }
        }

        var weaponOnly = new List<EquipmentSlot>();
        foreach (EquipmentSlot t in targetSlots)
            if (IsWeaponSlot(t) && !settled.Contains(t)) weaponOnly.Add(t);
        if (weaponOnly.Count > 0)
            foreach (OrderedCriterion criterion in criteria)
            {
                Take(criterion(weaponOnly, settled, now, budget));
                weaponOnly.RemoveAll(settled.Contains);
                if (weaponOnly.Count == 0) break;
            }

        foreach (OrderedCriterion criterion in criteria)
        {
            var free = new List<EquipmentSlot>();
            foreach (EquipmentSlot t in targetSlots)
                if (!settled.Contains(t)) free.Add(t);
            if (free.Count == 0) break;
            Take(criterion(free, settled, now, budget));
        }
        return result;
    }

    // Find Best for a criterion whose per-item scores don't add up across slots — the
    // computed backstab min / max, where the side fed by +min damage and BS min can
    // overtake the other and the realm then swaps (Paradigm) or clamps (Stock) the
    // range. Each pass ranks items by one additive score (push the min side, push the
    // max side, the average, the criterion's own score); every resulting set of picks
    // is priced as a whole by evaluate, and the best-scoring set wins.
    public static Dictionary<EquipmentSlot, string> FindBestOfPasses(
        IReadOnlyList<Func<ItemFinderEntry, double>> passes,
        Func<IReadOnlyDictionary<EquipmentSlot, string>, double> evaluate,
        IReadOnlyList<ItemFinderEntry> catalog,
        IReadOnlyList<EquipmentSlot> targetSlots,
        ISet<EquipmentSlot> heldSlots,
        IReadOnlyDictionary<EquipmentSlot, string?> current,
        int level, ClassEquipProfile cls, AlignmentBucket? alignment,
        Func<ItemFinderEntry, bool>? extraFilter = null,
        int? weightBudget = null,
        RealmType realm = RealmType.ParaMud,
        EvilPointRange? evilPoints = null)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(evaluate);
        Dictionary<EquipmentSlot, string> best = new();
        double bestValue = double.MinValue;
        foreach (Func<ItemFinderEntry, double> score in passes)
        {
            Dictionary<EquipmentSlot, string> picks = FindBest(catalog, targetSlots, heldSlots, current, score,
                level, cls, alignment, extraFilter, weightBudget, realm, evilPoints);
            double value = evaluate(picks);
            if (value > bestValue) (best, bestValue) = (picks, value);
        }
        return best;
    }
}
