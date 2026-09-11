using System;
using System.Collections.Generic;
using System.Linq;
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

    // One derived effect a base stat drives: a label, the marginal rate ("~N points
    // → +1"), and the function that computes the derived value so the exact next
    // breakpoint can be found by stepping the base stat (offsets / truncation quirks
    // handled by evaluating the real formula, not re-deriving them). Ratio overrides
    // the default "~PerPoint → +1 Label" text for stats whose marginal isn't a clean
    // 1-per-N (magic resist off Willpower is +3 per 4 points).
    private sealed record Effect(string Label, int PerPoint, Func<StatBlock, int> Compute, string? Ratio = null);

    private static IReadOnlyList<Effect> EffectsFor(BaseStat stat, RealmType realm)
    {
        bool para = realm == RealmType.ParaMud;
        return stat switch
        {
            BaseStat.Strength => new List<Effect>
            {
                new("min dmg", 10, MinDamageBonus),
                new("max dmg", 10, MaxDamageBonus),
                // Stock folds STR into normal accuracy (~3/pt); Paradigm normal does not.
                para ? null! : new Effect("accy", 3, s => AccuracyFromStats(s, realm)),
            }.Where(e => e is not null).ToList(),

            BaseStat.Agility => new List<Effect>
            {
                new("accy", para ? 3 : 6, s => AccuracyFromStats(s, realm)),
                new("dodge", 3, DodgeValue),
                new("crit", 20, CritRating),
                new("stealth", 4, Stealth),
            },

            BaseStat.Intellect => new List<Effect>
            {
                // Paradigm folds INT into normal accuracy (~6/pt); Stock does not.
                para ? new Effect("accy", 6, s => AccuracyFromStats(s, realm)) : null!,
                new("crit", 10, CritRating),
                new("stealth", 8, Stealth),
                new("magic res", 4, MagicResistance),
            }.Where(e => e is not null).ToList(),

            BaseStat.Charm => new List<Effect>
            {
                // Paradigm folds CHM into normal accuracy (~10/pt); Stock does not.
                para ? new Effect("accy", 10, s => AccuracyFromStats(s, realm)) : null!,
                new("dodge", 5, DodgeValue),
                new("crit", 30, CritRating),
                new("stealth", 6, Stealth),
            }.Where(e => e is not null).ToList(),

            // Willpower is the heaviest magic-res term: +3 per 4 points (~0.75/pt,
            // vs INT's ~0.25/pt). The generic "~N → +1" template would misread it,
            // so the ratio text is given explicitly; the breakpoint stepper is exact.
            BaseStat.Willpower => new List<Effect>
            {
                new("magic res", 1, MagicResistance, Ratio: "~4 → +3 magic res"),
            },

            // Health has no clean "per-N" combat breakpoint — its effect (max HP,
            // HP regen) is level-scaled and lives in the projection's HP columns.
            _ => Array.Empty<Effect>(),
        };
    }

    // ----- tooltip text ----------------------------------------------------

    // The mouseover tooltip for a base stat's CP-allocation column: a one-line
    // effect summary plus, per breakpoint effect, the next value of THIS stat that
    // ticks the derived stat up for the given character. `realm` selects Stock vs
    // Paradigm weighting. Health / Willpower show a plain-English effect line.
    public static string Tooltip(BaseStat stat, RealmType realm, StatBlock current)
    {
        var sb = new StringBuilder();
        sb.Append(HeaderFor(stat)).Append('\n');

        IReadOnlyList<Effect> effects = EffectsFor(stat, realm);
        if (effects.Count > 0)
        {
            sb.Append(string.Join(" · ", effects.Select(e => e.Ratio ?? $"~{e.PerPoint} → +1 {e.Label}")));
            int cur = current.Get(stat);
            if (cur > 0)
            {
                var next = effects
                    .Select(e => (e.Label, Value: NextBreakpoint(current, stat, cur, e.Compute)))
                    .Where(x => x.Value is > 0)
                    .OrderBy(x => x.Value)
                    .ToList();
                if (next.Count > 0)
                    sb.Append("\nNext from ").Append(cur).Append(": ")
                      .Append(string.Join(", ", next.Select(x => $"{x.Value} → +1 {x.Label}")));
            }
        }

        string extra = ExtraEffectLine(stat, realm);
        if (extra.Length > 0) sb.Append('\n').Append(extra);
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

    private static string HeaderFor(BaseStat stat) => stat switch
    {
        BaseStat.Strength => "Strength — melee damage, carry weight",
        BaseStat.Intellect => "Intellect — crit, magic resist, caster mana",
        BaseStat.Willpower => "Willpower — magic resist, caster mana",
        BaseStat.Agility => "Agility — accuracy, dodge, crit, stealth",
        BaseStat.Health => "Health — max HP, HP regen",
        _ => "Charm — dodge, crit, stealth, Bard mana",
    };

    // Effects that aren't clean per-point breakpoints (carry weight, HP/mana), shown
    // as a plain line under the ratios.
    private static string ExtraEffectLine(BaseStat stat, RealmType realm) => stat switch
    {
        BaseStat.Strength => "Carry weight +48/pt (steeper past 100)",
        BaseStat.Health => "Max HP and HP regen (scale with level)",
        BaseStat.Intellect => "Mage mana + spellcasting",
        BaseStat.Willpower => "Priest / Druid mana + spellcasting",
        BaseStat.Charm => "Bard mana",
        _ => string.Empty,
    };
}
