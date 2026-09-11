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

    // One derived effect a base stat drives, as one tooltip line: a label, the
    // pre-formatted marginal rate ("~6 AGL → +1", "+48 per pt", "≈ +3 HP/pt"), and
    // an optional Compute that yields the derived value so the exact next breakpoint
    // can be found by stepping the base stat (offsets / truncation handled by
    // evaluating the real formula). Compute null = descriptive line with no numeric
    // breakpoint (carry weight / spellcasting). ShowNext false suppresses the "next
    // at" for effects that rise every single point (max HP).
    private sealed record Effect(string Label, string Ratio, Func<StatBlock, int>? Compute, bool ShowNext = true);

    private static string Abbrev(BaseStat s) => s switch
    {
        BaseStat.Strength => "STR", BaseStat.Intellect => "INT", BaseStat.Willpower => "WIL",
        BaseStat.Agility => "AGL", BaseStat.Health => "HEA", _ => "CHM",
    };

    private static IReadOnlyList<Effect> EffectsFor(BaseStat stat, StatContext ctx, StatBlock current)
    {
        RealmType realm = ctx.Realm;
        bool para = realm == RealmType.ParaMud;
        string ab = Abbrev(stat);
        // Which base stat(s) scale mana regen, per magery type.
        bool manaFromInt = ctx.MageryType is 1 or 3;   // Mage, Druid
        bool manaFromWil = ctx.MageryType is 2 or 3;    // Priest, Druid
        bool manaFromChm = ctx.MageryType == 4;         // Bard

        Effect ManaRegen() => new("Mana regen",
            "casting stat", s => CharacterCalculator.CalcManaRegen(
                s.Level, s.Intellect, s.Willpower, s.Charm, ctx.MageryType, ctx.MageryLevel, 0, false, realm));

        var effects = new List<Effect>();
        switch (stat)
        {
            case BaseStat.Strength:
                effects.Add(new("Max melee dmg", $"~10 {ab} → +1 (above 50)", MaxDamageBonus));
                effects.Add(new("Min melee dmg", $"~10 {ab} → +1 (above 100)", MinDamageBonus));
                // Stock folds STR into normal accuracy (~3/pt); Paradigm normal does not.
                if (!para) effects.Add(new("Accuracy", $"~3 {ab} → +1", s => AccuracyFromStats(s, realm)));
                effects.Add(new("Carry weight", "+48 per pt (steeper past 100)", MaxEncumbrance, ShowNext: false));
                break;

            case BaseStat.Agility:
                effects.Add(new("Accuracy", $"~{(para ? 3 : 6)} {ab} → +1", s => AccuracyFromStats(s, realm)));
                effects.Add(new("Dodge", $"~3 {ab} → +1", DodgeValue));
                effects.Add(new("Crit", $"~20 {ab} → +1", CritRating));
                effects.Add(new("Stealth", $"~4 {ab} → +1", Stealth));
                break;

            case BaseStat.Intellect:
                // Paradigm folds INT into normal accuracy (~6/pt); Stock does not.
                if (para) effects.Add(new("Accuracy", $"~6 {ab} → +1", s => AccuracyFromStats(s, realm)));
                effects.Add(new("Crit", $"~10 {ab} → +1", CritRating));
                effects.Add(new("Stealth", $"~8 {ab} → +1", Stealth));
                effects.Add(new("Magic resist", $"+1 per 4 {ab}", MagicResistance));
                if (manaFromInt) effects.Add(ManaRegen());
                if (ctx.MageryType is 1 or 3) effects.Add(new("Spellcasting", "Mage / Druid casting stat", null));
                break;

            case BaseStat.Willpower:
                // WIL is the heaviest magic-res term: +3 per 4 points (INT's is +1 per 4).
                effects.Add(new("Magic resist", $"+3 per 4 {ab}", MagicResistance));
                // WIL does NOT raise MAX mana (that's level × magery only) — it scales
                // mana REGEN for Priests, and for Druids alongside INT.
                if (manaFromWil) effects.Add(ManaRegen());
                if (ctx.MageryType is 2 or 3) effects.Add(new("Spellcasting", "Priest / Druid casting stat", null));
                break;

            case BaseStat.Charm:
                // Paradigm folds CHM into normal accuracy (~10/pt); Stock does not.
                if (para) effects.Add(new("Accuracy", $"~10 {ab} → +1", s => AccuracyFromStats(s, realm)));
                effects.Add(new("Dodge", $"~5 {ab} → +1", DodgeValue));
                effects.Add(new("Crit", $"~30 {ab} → +1", CritRating));
                effects.Add(new("Stealth", $"~6 {ab} → +1", Stealth));
                if (manaFromChm) effects.Add(ManaRegen());
                if (ctx.MageryType == 4) effects.Add(new("Spellcasting", "Bard casting stat", null));
                break;

            case BaseStat.Health:
                // Max HP rises (nearly) every point, fractional and level-scaled, so
                // show the exact marginal at the current value — computed straight off
                // CalcMaxHp so it can't drift — rather than a discrete "next at".
                Func<StatBlock, int> maxHp = s => CharacterCalculator.CalcMaxHp(
                    s.Health, s.Level, ctx.MinHits, ctx.MaxHits, ctx.RaceHpPerLevel, 0, HpRollMode.Average);
                int perPoint = maxHp(current.With(BaseStat.Health, current.Health + 1)) - maxHp(current);
                effects.Add(new("Max HP", $"≈ +{Math.Max(0, perPoint)} per {ab} (at {current.Health})",
                    maxHp, ShowNext: false));
                effects.Add(new("HP regen", "idle, per tick",
                    s => CharacterCalculator.CalcHpRegen(s.Level, s.Health, 0, false, realm)));
                break;
        }
        return effects;
    }

    // ----- tooltip text ----------------------------------------------------

    // The mouseover tooltip for a base stat's CP-allocation column: a header naming
    // everything the stat affects, then one line per effect with its marginal rate
    // and, where it's a discrete breakpoint, the next value of THIS stat that ticks
    // it up for the given character. `ctx` supplies realm + the class/race data the
    // HP / mana-regen effects need.
    public static string Tooltip(BaseStat stat, StatBlock current, StatContext ctx)
    {
        IReadOnlyList<Effect> effects = EffectsFor(stat, ctx, current);
        var sb = new StringBuilder();
        sb.Append(HeaderFor(stat, effects));

        int cur = current.Get(stat);
        foreach (Effect e in effects)
        {
            sb.Append("\n• ").Append(e.Label).Append(" — ").Append(e.Ratio);
            if (e.Compute is { } compute && e.ShowNext && cur > 0)
            {
                int bp = NextBreakpoint(current, stat, cur, compute);
                if (bp > 0) sb.Append("  (next at ").Append(bp).Append(')');
            }
        }
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

    private static string HeaderFor(BaseStat stat, IReadOnlyList<Effect> effects)
    {
        string name = stat switch
        {
            BaseStat.Strength => "Strength", BaseStat.Intellect => "Intellect",
            BaseStat.Willpower => "Willpower", BaseStat.Agility => "Agility",
            BaseStat.Health => "Health", _ => "Charm",
        };
        if (effects.Count == 0) return name;
        return $"{name} affects:";
    }
}
