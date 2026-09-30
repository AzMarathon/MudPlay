using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One hunting area's simulated result at a level. An area where any run died or
// hung up is unsafe: its exp/hr only counts the minutes before the run ended, so
// it ranks after every safe area however high the number looks.
public sealed record AreaRank(string Area, IReadOnlyList<RoomKey> Tour, int LapRooms, LoopSimSummary Result)
{
    public bool Safe => Result.Deaths == 0 && Result.HangUps == 0;

    public string Label => Safe
        ? $"{Area} — {Result.ExpPerHour:N0}/hr · low HP {Result.LowestHpPercent}% · {Tour.Count} lairs"
        : $"{Area} — died in {Result.Deaths + Result.HangUps} of {Result.Runs.Count} runs";

    // Safe areas by exp/hr, then the unsafe ones by exp/hr.
    public static IReadOnlyList<AreaRank> Rank(IEnumerable<AreaRank> ranks) =>
        ranks.OrderByDescending(r => r.Safe).ThenByDescending(r => r.Result.ExpPerHour).ToList();
}
