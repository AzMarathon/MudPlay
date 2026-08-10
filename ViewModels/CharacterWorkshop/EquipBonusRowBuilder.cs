using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The ordered "stats from your worn items" readout, shared by the Equipment
// Manager (its Equipment Bonuses box) and the Item Finder's trial gearset panel so
// the two present an identical list. Emits one EquipBonusRow per NON-ZERO stat in
// a fixed order, each with a per-item source tooltip. Callers that also show a
// projected-AC line (the Equipment Manager) compute that separately.
public static class EquipBonusRowBuilder
{
    public static List<EquipBonusRow> Build(EquipmentStatBreakdown b)
    {
        var rows = new List<EquipBonusRow>();
        EquipmentStatSummary t = b.Totals;

        // Worn AC and blur AC are shown as separate lines — blur is encumbrance-scaled,
        // not flat armour — so the "Armour Class" figure excludes the blur portion.
        AddDouble(rows, b, "Armour Class", t.PlusAC - t.PlusAcBlur);
        AddDouble(rows, b, "AC Blur", t.PlusAcBlur);
        AddDouble(rows, b, "Damage Resist", t.PlusDR);
        AddInt(rows, b, "Strength", t.PlusStrength);
        AddInt(rows, b, "Intellect", t.PlusIntellect);
        AddInt(rows, b, "Willpower", t.PlusWillpower);
        AddInt(rows, b, "Agility", t.PlusAgility);
        AddInt(rows, b, "Health", t.PlusHealth);
        AddInt(rows, b, "Charm", t.PlusCharm);
        AddInt(rows, b, "Max HP", t.PlusMaxHp);
        AddInt(rows, b, "Max Mana", t.PlusMaxMana);
        AddInt(rows, b, "HP Regen", t.HpRegenPercent);
        AddInt(rows, b, "Mana Regen", t.MpRegenPercent);
        AddInt(rows, b, "Crits", t.PlusCrits);
        AddAccuracy(rows, b, t);
        AddInt(rows, b, "Max Damage", t.PlusMaxDamage);
        AddInt(rows, b, "Spell Damage", t.SpellDamageBonus);
        AddInt(rows, b, "Hit Magic", t.WeaponHitMagic);
        AddInt(rows, b, "Dodge", t.PlusDodge);
        AddInt(rows, b, "Magic Resist", t.PlusMagicResist);
        AddInt(rows, b, "BS Accuracy", t.PlusBSAccuracy);
        AddInt(rows, b, "BS Min Dmg", t.PlusBSMin);
        AddInt(rows, b, "BS Max Dmg", t.PlusBSMax);
        AddInt(rows, b, "Stealth", t.PlusStealth);
        AddInt(rows, b, "Perception", t.PlusPerception);
        AddInt(rows, b, "Spellcasting", t.PlusSpellcasting);
        AddInt(rows, b, "Encumbrance", t.PlusEncumbrance);
        AddInt(rows, b, "Traps", t.PlusTraps);
        AddInt(rows, b, "Picklocks", t.PlusPicklocks);
        AddInt(rows, b, "Illuminate", t.PlusIlluminate);
        AddInt(rows, b, "Quickness", t.PlusQuickness);
        AddInt(rows, b, "Cold Resist", t.PlusColdResist);
        AddInt(rows, b, "Fire Resist", t.PlusFireResist);
        AddInt(rows, b, "Stone Resist", t.PlusStoneResist);
        AddInt(rows, b, "Lightning Resist", t.PlusLightningResist);
        AddInt(rows, b, "Water Resist", t.PlusWaterResist);
        AddInt(rows, b, "Prot Evil", t.PlusProtEvil);
        AddInt(rows, b, "Prot Good", t.PlusProtGood);
        AddInt(rows, b, "Punch Dmg", t.PlusPunchDmg);
        AddInt(rows, b, "Punch Accy", t.PlusPunchAccy);
        AddInt(rows, b, "Kick Dmg", t.PlusKickDmg);
        AddInt(rows, b, "Kick Accy", t.PlusKickAccy);
        AddInt(rows, b, "JumpKick Dmg", t.PlusJumpKickDmg);
        AddInt(rows, b, "JumpKick Accy", t.PlusJumpKickAccy);
        return rows;
    }

    private static void AddInt(List<EquipBonusRow> rows, EquipmentStatBreakdown b, string statKey, int value)
    {
        if (value == 0) return;
        rows.Add(new EquipBonusRow(statKey, value.ToString("+0;-0", CultureInfo.InvariantCulture), Tooltip(b, statKey)));
    }

    private static void AddDouble(List<EquipBonusRow> rows, EquipmentStatBreakdown b, string statKey, double value)
    {
        if (value == 0) return;
        rows.Add(new EquipBonusRow(statKey, value.ToString("+0.#;-0.#", CultureInfo.InvariantCulture), Tooltip(b, statKey)));
    }

    // Accuracy total combines worn-item Accy fields with the abil-22 sum — the same
    // number Character Info feeds the accuracy formula.
    private static void AddAccuracy(List<EquipBonusRow> rows, EquipmentStatBreakdown b, EquipmentStatSummary t)
    {
        int total = t.TotalWornAccy + t.PlusAccuracy;
        if (total == 0) return;
        rows.Add(new EquipBonusRow("Accuracy", total.ToString("+0;-0", CultureInfo.InvariantCulture), Tooltip(b, "Accuracy")));
    }

    private static string? Tooltip(EquipmentStatBreakdown b, string statKey)
    {
        if (!b.PerStatSources.TryGetValue(statKey, out var sources) || sources.Count == 0)
            return null;
        var sb = new StringBuilder();
        foreach (StatContribution s in sources)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s.ItemName).Append("  ").Append(s.DisplayValue);
            if (!string.IsNullOrEmpty(s.Tag)) sb.Append(' ').Append(s.Tag);
        }
        return sb.ToString();
    }
}
