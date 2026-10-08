using System.Collections.Generic;
using System.Globalization;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Words an AttackEstimate for the Gear Finder's trial damage readout. Only the lines
// an attack type has are written: a backstab is one strike with no crit, and bash and
// smash never crit (GAME_MECHANICS "Bash and smash damage vs DR"), so neither gets a
// crit or Quick & Deadly line.
public static class TrialDamageText
{
    public static IReadOnlyList<TrialDamageRow> Rows(MudAttackType type, AttackEstimate est)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        bool backstab = type == MudAttackType.Backstab;
        var rows = new List<TrialDamageRow>
        {
            new("Accuracy", est.Accuracy.ToString(inv)),
            new("Hit chance", string.Create(inv, $"{est.HitPercent}%"),
                "The chance one swing lands on this target, its dodge counted."),
        };

        bool resisted = est.MinAfterDr != est.MinDamage || est.MaxAfterDr != est.MaxDamage;
        rows.Add(new TrialDamageRow(
            backstab ? "Backstab damage" : "Damage / hit",
            string.Create(inv, $"{est.MinAfterDr}-{est.MaxAfterDr}"),
            resisted
                ? string.Create(inv, $"{est.MinDamage}-{est.MaxDamage} before the target's damage resist.")
                : null));

        if (!backstab)
            rows.Add(new TrialDamageRow("Swings / round", est.SwingsPerRound.ToString("0.##", inv)));

        if (type == MudAttackType.Normal)
        {
            rows.Add(new TrialDamageRow("Crit chance", string.Create(inv, $"{est.CritChance}%")));
            rows.Add(new TrialDamageRow("Quick & Deadly", string.Create(inv, $"+{est.QuickAndDeadlyBonus}"),
                "The crit a fast swing adds. It is already in the crit chance above."));
        }

        rows.Add(backstab
            ? new TrialDamageRow("Expected / stab", est.DamagePerRound.ToString("0.#", inv),
                "The average stab times its chance to land.")
            : new TrialDamageRow("Damage / round", est.DamagePerRound.ToString("0.#", inv),
                type == MudAttackType.Normal
                    ? "Expected damage a round: misses, dodges and crits counted."
                    : "Expected damage a round: misses and dodges counted."));
        return rows;
    }
}
