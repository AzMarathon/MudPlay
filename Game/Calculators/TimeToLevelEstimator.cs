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

    // BankableLevels = levels the running exp already covers but hasn't trained;
    // TargetLevel = the first not-yet-reached level (0 when the exp chart can't be
    // resolved); Eta = time to reach it at the rate (null when unresolvable, Zero
    // when already there).
    public readonly record struct Result(int BankableLevels, int TargetLevel, TimeSpan? Eta);

    public static Result Estimate(PlayerStats stats, GameDataCache gameData, double ratePerHour)
    {
        if (stats is null || gameData is null || stats.Level <= 0) return new(0, 0, null);

        int chart = ExperienceTableCalculator.CalcExpChart(
            GetInt(gameData.FindRowByName("Classes", stats.Class), "ExpTable"),
            GetInt(gameData.FindRowByName("Races", stats.Race), "ExpTable"));
        if (chart <= 0) return new(0, 0, null);

        RealmType realm = gameData.ActiveRealm;
        long exp = stats.Exp;
        int banked = TrainBudgetCalculator.BankableLevels(exp, stats.Level, chart, realm, MaxLevelScan);
        int target = stats.Level + banked + 1;
        TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(
            ExperienceTableCalculator.CalcExpNeeded(target, chart, realm), exp, (long)ratePerHour);
        return new(banked, target, eta);
    }

    private static int GetInt(JsonElement? rowOpt, string property)
    {
        if (rowOpt is not JsonElement row || row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
