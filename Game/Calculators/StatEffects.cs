using System;
using System.Collections.Generic;
using System.Text;

namespace MudPlay.Game.Calculators;

// The six base stats a snapshot needs for the derived-stat math.
public readonly record struct StatBlock(
    int Level, int Strength, int Intellect, int Willpower, int Agility, int Health, int Charm)
{
    // Return a copy with one base stat overridden — used to step a single stat when
    // finding the next breakpoint.
    public StatBlock With(BaseStat stat, int value) => stat switch
    {
        BaseStat.Strength => this with { Strength = value },
        BaseStat.Intellect => this with { Intellect = value },
        BaseStat.Willpower => this with { Willpower = value },
        BaseStat.Agility => this with { Agility = value },
        BaseStat.Health => this with { Health = value },
        _ => this with { Charm = value },
    };

    public int Get(BaseStat stat) => stat switch
    {
        BaseStat.Strength => Strength,
        BaseStat.Intellect => Intellect,
        BaseStat.Willpower => Willpower,
        BaseStat.Agility => Agility,
        BaseStat.Health => Health,
        _ => Charm,
    };
}

public enum BaseStat { Strength, Intellect, Willpower, Agility, Health, Charm }

// The class/race-derived context the HP and mana-regen breakpoints need on top of
// a StatBlock: realm, the class hit-dice + race per-level HP (for max-HP growth),
// and the magery type/level (for mana-regen scaling). MageryType maps the casting
// stat: 1=INT (Mage), 2=WIL (Priest), 3=(INT+WIL)/2 (Druid), 4=CHM (Bard), 5=Kai
// (Mystic, fixed-rate), 0=non-caster.
public readonly record struct StatContext(
    RealmType Realm,
    int MinHits, int MaxHits, int RaceHpPerLevel,
    int MageryType, int MageryLevel);

// The stat-derived secondary numbers a combat profile / CP plan cares about — the
// gear-independent, stat-and-level portion. All sourced from the existing verified
// calculators (CombatCalculator / CharacterCalculator), so the CP-tab tooltips and
// the Level Projection grid can never drift from what the combat engine uses. The
// accuracy / dodge / damage branches are realm-verified; crit, encumbrance and
// magic-resistance use the stock DLL formula for both realms and are flagged
// unverified for Paradigm in the guide.
public static class StatEffects
{
    // ----- derived values (the projection columns) -------------------------

    // Normal-attack accuracy from STATS only (no level base, gear, or encumbrance) —
    // the per-stat "Accy" contribution the PNG breakpoint table shows. Realm-split
    // to match CombatCalculator.CalcAccuracy: Stock weights STR + AGI; Paradigm
    // normal weights AGI + INT + CHM.
    public static int AccuracyFromStats(StatBlock s, RealmType realm)
    {
        if (realm == RealmType.ParaMud)
            return (s.Agility - 50) / 3 + (s.Intellect - 50) / 6 + (s.Charm - 50) / 10;
        return (s.Strength - 50) / 3 + (s.Agility - 50) / 6;
    }

    // Bash / smash accuracy stat contribution — STR/3 + AGL/6, the same on both
    // realms (CalcAccuracy's bash/smash branch weights STR + AGL and drops INT/CHM).
    // On Stock this equals the normal-attack contribution; on Paradigm it's how STR
    // reaches accuracy at all (normal Paradigm attacks get no STR accuracy).
    public static int BashAccuracyFromStats(StatBlock s)
        => (s.Strength - 50) / 3 + (s.Agility - 50) / 6;

    public static int CritRating(StatBlock s)
        => CharacterCalculator.CalcBaseCritRating(s.Level, s.Intellect, s.Agility, s.Charm);

    // Raw dodge value (before the vs-accuracy % conversion) — level/5 + CHM/5 + AGI/3.
    public static int DodgeValue(StatBlock s)
        => CombatCalculator.CalcDodge(s.Level, s.Agility, s.Charm, plusDodge: 0);

    public static int Stealth(StatBlock s)
        => CharacterCalculator.CalcStealthBase(s.Level, s.Intellect, s.Agility, s.Charm);

    // STR's fold into weapon min / max damage (the bonus added to the weapon's own
    // range): min (STR-100)/10, max (STR-50)/10, never negative (GreaterMUD floor).
    public static int MinDamageBonus(StatBlock s) => Math.Max(0, (s.Strength - 100) / 10);
    public static int MaxDamageBonus(StatBlock s) => Math.Max(0, (s.Strength - 50) / 10);

    public static int MaxEncumbrance(StatBlock s) => CharacterCalculator.CalcMaxEncumbrance(s.Strength);
    public static int MagicResistance(StatBlock s) => CharacterCalculator.CalcMagicResistance(s.Intellect, s.Willpower);

    // ----- per-stat effect lines (tooltips) --------------------------------

    private static string Abbrev(BaseStat s) => s switch
    {
        BaseStat.Strength => "STR", BaseStat.Intellect => "INT", BaseStat.Willpower => "WIL",
        BaseStat.Agility => "AGL", BaseStat.Health => "HEA", _ => "CHM",
    };

    // ----- tooltip text ----------------------------------------------------

    // The mouseover tooltip for a base stat's CP-allocation column: a header naming
    // the stat, then one line per effect showing the CURRENT derived value, its
    // marginal rate, and — where it's a discrete breakpoint — the next value of THIS
    // stat that ticks it up for the character. Values are the stat-and-level portion
    // (gear / quests stack on top in-game). `ctx` supplies realm + the class/race
    // data the HP and mana-regen / spellcasting effects need. Caster effects show
    // ONLY under the class's casting stat(s): Mage INT, Priest WIL, Druid INT+WIL,
    // Bard CHM.
    public static string Tooltip(BaseStat stat, StatBlock current, StatContext ctx)
    {
        RealmType realm = ctx.Realm;
        bool para = realm == RealmType.ParaMud;
        string ab = Abbrev(stat);
        int cur = current.Get(stat);
        bool manaFromInt = ctx.MageryType is 1 or 3;   // Mage, Druid
        bool manaFromWil = ctx.MageryType is 2 or 3;    // Priest, Druid
        bool manaFromChm = ctx.MageryType == 4;         // Bard

        // Derived-value functions (stat-and-level portion; gear excluded).
        Func<StatBlock, int> accy = s => AccuracyFromStats(s, realm);
        Func<StatBlock, int> maxHp = s => CharacterCalculator.CalcMaxHp(
            s.Health, s.Level, ctx.MinHits, ctx.MaxHits, ctx.RaceHpPerLevel, 0, HpRollMode.Average);
        Func<StatBlock, int> hpIdle = s => CharacterCalculator.CalcHpRegen(s.Level, s.Health, 0, false, realm);
        Func<StatBlock, int> hpRest = s => CharacterCalculator.CalcHpRegen(s.Level, s.Health, 0, true, realm);
        Func<StatBlock, int> manaRegen = s => CharacterCalculator.CalcManaRegen(
            s.Level, s.Intellect, s.Willpower, s.Charm, ctx.MageryType, ctx.MageryLevel, 0, false, realm);
        Func<StatBlock, int> spellcast = s => CharacterCalculator.CalcSpellcasting(
            s.Level, s.Intellect, s.Willpower, s.Charm, ctx.MageryType, ctx.MageryLevel, 0);

        var lines = new List<string>();
        // "next at N" suffix for a derived function (empty if none within range).
        string Next(Func<StatBlock, int> f)
        {
            if (cur <= 0) return "";
            int bp = NextBreakpoint(current, stat, cur, f);
            return bp > 0 ? $"next at {bp}" : "";
        }
        // Compose one line: "{label} — {value}  ({rate}, {next})", dropping empties.
        void Line(string label, string value, string rate, Func<StatBlock, int>? bp)
        {
            var parts = new List<string>();
            if (rate.Length > 0) parts.Add(rate);
            if (bp is not null) { string n = Next(bp); if (n.Length > 0) parts.Add(n); }
            string tail = parts.Count > 0 ? $"  ({string.Join(", ", parts)})" : "";
            lines.Add($"{label} — {value}{tail}");
        }

        switch (stat)
        {
            case BaseStat.Strength:
                Line("Max melee dmg", $"+{MaxDamageBonus(current)}", $"~10 {ab} → +1 above 50", MaxDamageBonus);
                Line("Min melee dmg", $"+{MinDamageBonus(current)}", $"~10 {ab} → +1 above 100", MinDamageBonus);
                // Stock: STR feeds accuracy on ALL attacks. Paradigm: only bash/smash.
                if (!para)
                    Line("Accuracy", accy(current).ToString("+0;-0;0"), $"~3 {ab} → +1 (all attacks)", accy);
                else
                    Line("Bash/smash accy", BashAccuracyFromStats(current).ToString("+0;-0;0"),
                        $"~3 {ab} → +1 (bash/smash only)", BashAccuracyFromStats);
                int encNow = MaxEncumbrance(current);
                int encPer = MaxEncumbrance(current.With(BaseStat.Strength, current.Strength + 1)) - encNow;
                Line("Carry weight", $"{encNow} max", $"+{encPer}/pt here (+48 to 100 {ab}, +84 beyond)", null);
                break;

            case BaseStat.Agility:
                // AGL feeds accuracy on every attack in both realms (Paradigm normal
                // weights it /3, bash/smash /6; Stock /6 throughout).
                Line("Accuracy", accy(current).ToString("+0;-0;0"),
                    para ? $"~3 {ab} → +1 (normal; ~6 bash/smash)" : $"~6 {ab} → +1 (all attacks)", accy);
                Line("Dodge", $"{DodgeValue(current)}", $"~3 {ab} → +1", DodgeValue);
                Line("Crit", $"{CritRating(current)}%", $"~20 {ab} → +1", CritRating);
                Line("Stealth", $"{Stealth(current)}", $"~4 {ab} → +1", Stealth);
                break;

            case BaseStat.Intellect:
                // INT feeds accuracy on Paradigm NORMAL attacks only — never Stock,
                // never bash/smash.
                if (para) Line("Accuracy", accy(current).ToString("+0;-0;0"), $"~6 {ab} → +1 (normal attacks)", accy);
                Line("Crit", $"{CritRating(current)}%", $"~10 {ab} → +1", CritRating);
                Line("Stealth", $"{Stealth(current)}", $"~8 {ab} → +1", Stealth);
                Line("Magic resist", $"{MagicResistance(current)}", $"+1 per 4 {ab}", MagicResistance);
                if (manaFromInt) Line("Mana regen", $"{manaRegen(current)}/tick", "", manaRegen);
                if (manaFromInt) Line("Spellcasting", $"{spellcast(current)}", "", spellcast);
                break;

            case BaseStat.Willpower:
                // WIL is the heaviest magic-res term (+3 per 4). It scales mana REGEN
                // and spellcasting for Priests/Druids — NOT max mana (level × magery).
                Line("Magic resist", $"{MagicResistance(current)}", $"+3 per 4 {ab}", MagicResistance);
                if (manaFromWil) Line("Mana regen", $"{manaRegen(current)}/tick", "", manaRegen);
                if (manaFromWil) Line("Spellcasting", $"{spellcast(current)}", "", spellcast);
                break;

            case BaseStat.Charm:
                // CHM feeds accuracy on Paradigm NORMAL attacks only.
                if (para) Line("Accuracy", accy(current).ToString("+0;-0;0"), $"~10 {ab} → +1 (normal attacks)", accy);
                Line("Dodge", $"{DodgeValue(current)}", $"~5 {ab} → +1", DodgeValue);
                Line("Crit", $"{CritRating(current)}%", $"~30 {ab} → +1", CritRating);
                Line("Stealth", $"{Stealth(current)}", $"~6 {ab} → +1", Stealth);
                if (manaFromChm) Line("Mana regen", $"{manaRegen(current)}/tick", "", manaRegen);
                if (manaFromChm) Line("Spellcasting", $"{spellcast(current)}", "", spellcast);
                break;

            case BaseStat.Health:
                int hpPer = maxHp(current.With(BaseStat.Health, current.Health + 1)) - maxHp(current);
                Line("Max HP", $"{maxHp(current)}", $"≈ +{Math.Max(0, hpPer)} per {ab} here", null);
                Line("HP regen", $"{hpIdle(current)} idle / {hpRest(current)} rest per tick", "", hpIdle);
                break;
        }

        var sb = new StringBuilder(HeaderFor(stat, lines.Count));
        foreach (string line in lines) sb.Append("\n• ").Append(line);
        return sb.ToString();
    }

    // Smallest value > cur (search bounded) at which the derived function ticks up.
    // Evaluates the real formula each step so offset / integer-truncation quirks are
    // exact. Returns 0 if none within the search window.
    private static int NextBreakpoint(StatBlock block, BaseStat stat, int cur, Func<StatBlock, int> compute)
    {
        int baseline = compute(block);
        for (int v = cur + 1; v <= cur + 60; v++)
            if (compute(block.With(stat, v)) > baseline) return v;
        return 0;
    }

    private static string HeaderFor(BaseStat stat, int lineCount)
    {
        string name = stat switch
        {
            BaseStat.Strength => "Strength", BaseStat.Intellect => "Intellect",
            BaseStat.Willpower => "Willpower", BaseStat.Agility => "Agility",
            BaseStat.Health => "Health", _ => "Charm",
        };
        return lineCount == 0 ? name : $"{name} affects (from stats + level; gear/quests add on top):";
    }
}
