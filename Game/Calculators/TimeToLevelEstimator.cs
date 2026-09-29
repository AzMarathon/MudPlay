using System;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Calculators;

// The single time-to-next-level estimate shared by the Session Stats readout and
// the status-bar "TNL" so the two can't drift. Targets the first level the running
// exp hasn't reached yet — accounting for banked-but-untrained levels, which the
// raw stat-line "exp to next" ignores — at the given exp/hour rate. Returns null
// when the level / rate / exp chart can't be resolved, TimeSpan.Zero when already
// there. Resolves the exp chart from game data (mirrors the caller-resolves-chart
// convention; the pure ExperienceTableCalculator never reads game data).
public static class TimeToLevelEstimator
{
    private const int MaxLevelScan = 60;

    // BankableLevels = whole levels the running exp already covers but hasn't
    // trained; BankableLevelsFractional = the same as a fraction (whole + progress
    // toward the next level), the "(+N.NN)" the status-bar TNL shows; TargetLevel =
    // the first not-yet-reached level (0 when the exp chart can't be resolved); Eta =
    // time to reach it at the rate (null when unresolvable, Zero when already there);
    // ExpNeeded = the exp still to earn to reach TargetLevel (0 when unresolvable).
    public readonly record struct Result(int BankableLevels, double BankableLevelsFractional, int TargetLevel, TimeSpan? Eta, long ExpNeeded);

    public static Result Estimate(PlayerStats stats, GameDataCache gameData, double ratePerHour)
    {
        if (stats is null || gameData is null || stats.Level <= 0) return new(0, 0, 0, null, 0);

        int chart = ExperienceTableCalculator.CalcExpChart(
            GetInt(gameData.FindRowByName("Classes", stats.Class), "ExpTable"),
            GetInt(gameData.FindRowByName("Races", stats.Race), "ExpTable"));
        if (chart <= 0) return new(0, 0, 0, null, 0);

        RealmType realm = gameData.ActiveRealm;
        long exp = stats.Exp;
        int banked = TrainBudgetCalculator.BankableLevels(exp, stats.Level, chart, realm, MaxLevelScan);
        double bankedFrac = TrainBudgetCalculator.BankableLevelsFractional(exp, stats.Level, chart, realm, MaxLevelScan);
        int target = stats.Level + banked + 1;
        long targetExp = ExperienceTableCalculator.CalcExpNeeded(target, chart, realm);
        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(targetExp, exp, (long)ratePerHour);
        return new(banked, bankedFrac, target, eta, Math.Max(0, targetExp - exp));
    }

    private static int GetInt(JsonElement? rowOpt, string property)
    {
        if (rowOpt is not JsonElement row || row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
