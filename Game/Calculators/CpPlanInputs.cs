using MudPlay.Models.Profile;

namespace MudPlay.Game.Calculators;

// What the CP plan grid is worked from, all off one `stat` reading: the trained-stat
// baseline, the unspent CP, and the level whose training that CP already counts.
// Held is true when they come from an earlier reading because the latest one can't
// be worked back to the trained stats.
public readonly record struct CpPlanInputs(CpPlanEntry Baseline, int Cp, int CpThroughLevel, bool Held)
{
    public string Describe() =>
        $"STR {Baseline.Strength} INT {Baseline.Intellect} WIL {Baseline.Willpower} AGL {Baseline.Agility} "
        + $"HEA {Baseline.Health} CHM {Baseline.Charm}, {Cp} CP counted through level {CpThroughLevel}";
}
