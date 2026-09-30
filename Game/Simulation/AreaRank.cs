using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One ranked hunting option at Level: an area's whole lair tour, or one of the
// user's saved loops (IsLoop), with Live — what the user actually earned on that
// loop, from the program logs, at the ranked level and their biggest sample near
// it — beside the simulation. An option where a simulated run died or hung up is
// unsafe (its exp/hr only counts the minutes before the run ended) and ranks after
// every safe one — unless the user has played it for hours at the ranked level or
// below without dying, which outweighs the simulation. A record from a higher
// level is shown but proves nothing about surviving at this one.
public sealed record AreaRank(
    string Area, int Level, IReadOnlyList<RoomKey> Tour, int LapRooms, LoopSimSummary Result,
    bool IsLoop = false, IReadOnlyList<LiveLoopRecord>? Live = null)
{
    public bool Safe => (Result.Deaths == 0 && Result.HangUps == 0) || PlayedSafely;

    private bool PlayedSafely =>
        Live?.Where(r => r.Level <= Level).ToList() is { Count: > 0 } atOrBelow && atOrBelow.All(r => r.Deaths == 0);

    public string Label
    {
        get
        {
            string name = IsLoop ? $"★ {Area} (your loop)" : Area;
            string live = Live is { Count: > 0 } l
                ? " · you made " + string.Join(", ", l.Select(r => $"{r.ExpPerHour:N0}/hr over {r.Hours:0.#} h at L{r.Level}"))
                : "";
            if (Result.Deaths + Result.HangUps > 0 && !Safe)
                return $"{name} — died in {Result.Deaths + Result.HangUps} of {Result.Runs.Count} runs{live}";
            string where = IsLoop ? $"{LapRooms} rooms" : $"{Tour.Count} lairs";
            return $"{name} — {Result.ExpPerHour:N0}/hr simulated{live} · low HP {Result.LowestHpPercent}% · {where}";
        }
    }

    // Safe options by exp/hr, then the unsafe ones by exp/hr.
    public static IReadOnlyList<AreaRank> Rank(IEnumerable<AreaRank> ranks) =>
        ranks.OrderByDescending(r => r.Safe).ThenByDescending(r => r.Result.ExpPerHour).ToList();
}
