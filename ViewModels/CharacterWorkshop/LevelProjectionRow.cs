using System.Globalization;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One display row in the Level Projection grid — formats a LevelProjection into
// the grid's string columns and carries the IsCurrentLevel flag the view uses to
// highlight the live character's current level.
public sealed class LevelProjectionRow
{
    public int Level { get; }
    // Exp remaining to reach this level from the character's current exp
    // (max(0, TotalXp − currentExp)). 0 once you already hold enough — equals
    // TotalXp when current exp is 0.
    public string ExpToLevel { get; }
    // Cumulative exp threshold to reach this level (absolute).
    public string TotalXp { get; }
    public string HpRange { get; }
    // HP regen per tick as "idle / resting" (resting is 3× idle) — so the column
    // shows both the passive and the sit-and-rest rate.
    public string HpRegen { get; }
    // Max mana (or Kai for Mystics); "—" for non-casters.
    public string Mana { get; }
    // Mana/Kai regen per tick; "—" for non-casters.
    public string MpRegen { get; }
    // Cheapest trainer's copper fee to reach this level; "—" when no trainer
    // serves it for this class (quest-gated level or beyond the top trainer).
    public string Cost { get; }

    // Derived combat/utility stats — the gear-free stat-and-level portion, shown
    // so the CP plan's per-level stat increases surface their effect the same way
    // HP/Mana already do. Accuracy is the normal-attack stat contribution.
    public string Accuracy { get; }
    public string Crit { get; }
    public string Dodge { get; }
    public string Stealth { get; }
    // STR's bonus onto the weapon's own min/max damage range.
    public string MeleeDmg { get; }
    public string MaxEnc { get; }
    public string MagicRes { get; }

    // Stat-and-level utility skills, off by default in the column picker. The
    // thief four are computed for any class — whether the character can USE them
    // is a class/race grant, so the picker (not the grid) decides who sees them.
    public string Perception { get; }
    public string Thievery { get; }
    public string Traps { get; }
    public string Picklocks { get; }
    // Spellcasting skill; "—" for non-casters and Mystics (Kai has no skill).
    public string Spellcasting { get; }
    public string Tracking { get; }
    // Backstab accuracy; "—" for a class/race with no stealth source.
    public string BsAccuracy { get; }

    // True when this row is the live character's current level.
    public bool IsCurrentLevel { get; }

    public LevelProjectionRow(LevelProjection p, long currentExp, bool isCurrentLevel, bool isCaster, long? trainCost)
    {
        Level = p.Level;
        TotalXp = FormatExp(p.TotalXp);
        long remaining = p.TotalXp == long.MaxValue
            ? long.MaxValue
            : System.Math.Max(0, p.TotalXp - currentExp);
        ExpToLevel = FormatExp(remaining);
        HpRange = p.HpMin == p.HpMax
            ? p.HpMin.ToString("N0", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{p.HpMin:N0}–{p.HpMax:N0}");
        HpRegen = string.Create(CultureInfo.InvariantCulture, $"{p.HpRegen} / {p.HpRegenResting}");
        Mana = isCaster ? p.Mana.ToString("N0", CultureInfo.InvariantCulture) : "—";
        MpRegen = isCaster ? p.MpRegen.ToString(CultureInfo.InvariantCulture) : "—";
        // Raw copper, ungrouped (no thousands separators) — the train cost pastes
        // straight into the game, unlike the other columns' human-readable figures.
        Cost = trainCost is { } c ? c.ToString("0", CultureInfo.InvariantCulture) : "—";

        Accuracy = p.Accuracy.ToString(CultureInfo.InvariantCulture);
        Crit = string.Create(CultureInfo.InvariantCulture, $"{p.Crit}%");
        Dodge = p.Dodge.ToString(CultureInfo.InvariantCulture);
        Stealth = p.Stealth.ToString(CultureInfo.InvariantCulture);
        MeleeDmg = string.Create(CultureInfo.InvariantCulture, $"+{p.MinDmg}/+{p.MaxDmg}");
        MaxEnc = p.MaxEnc.ToString("N0", CultureInfo.InvariantCulture);
        MagicRes = p.MagicRes.ToString(CultureInfo.InvariantCulture);

        Perception = p.Perception.ToString(CultureInfo.InvariantCulture);
        Thievery = p.Thievery.ToString(CultureInfo.InvariantCulture);
        Traps = p.Traps.ToString(CultureInfo.InvariantCulture);
        Picklocks = p.Picklocks.ToString(CultureInfo.InvariantCulture);
        Tracking = p.Tracking.ToString(CultureInfo.InvariantCulture);
        // CalcSpellcasting returns 0 for non-casters and Mystics alike — show the
        // same "—" the Mana column uses rather than a misleading 0.
        Spellcasting = p.Spellcasting > 0
            ? p.Spellcasting.ToString(CultureInfo.InvariantCulture)
            : "—";
        BsAccuracy = p.BsAccuracy is { } bs ? bs.ToString(CultureInfo.InvariantCulture) : "—";

        IsCurrentLevel = isCurrentLevel;
    }

    // The exp formulas saturate at long.MaxValue instead of overflowing; show
    // that ceiling as ∞ rather than a meaningless 9.2-quintillion figure.
    private static string FormatExp(long value) =>
        value == long.MaxValue ? "∞" : value.ToString("N0", CultureInfo.InvariantCulture);
}
