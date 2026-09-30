namespace MudPlay.Game.Simulation;

// One simulated run of a loop: what the character earned and where the time went,
// the same measures Session Stats keeps live. A run that dies stops there
// (DiedAtSeconds) — the gear is on the corpse, so nothing after it would count.
// Per-hour rates divide by the hours the run was asked to play, not the hours it
// lasted: a death earns nothing for the rest of the session, so a run that dies
// early pulls the average down instead of reporting its pre-death pace.
public sealed record LoopSimRun(
    double RequestedSeconds,
    double Seconds,
    long Exp,
    int Kills,
    int Laps,
    double MovingSeconds,
    double AttackingSeconds,
    double RestingSeconds,
    double MeditatingSeconds,
    double WaitingSeconds,
    int LowestHpPercent,
    int LowestManaPercent,
    double? DiedAtSeconds,
    IReadOnlyDictionary<string, int> Casts)
{
    public double RequestedHours => RequestedSeconds / 3600.0;
    public double ExpPerHour => RequestedHours > 0 ? Exp / RequestedHours : 0;
    public double KillsPerHour => RequestedHours > 0 ? Kills / RequestedHours : 0;
    public double AvgLapSeconds => Laps > 0 ? Seconds / Laps : 0;
}

// Several seeded runs of the same loop, summarised: the spread is the answer to
// "how much of that number is luck".
public sealed record LoopSimSummary(IReadOnlyList<LoopSimRun> Runs)
{
    public double ExpPerHour => Runs.Count > 0 ? Runs.Average(r => r.ExpPerHour) : 0;
    public double MinExpPerHour => Runs.Count > 0 ? Runs.Min(r => r.ExpPerHour) : 0;
    public double MaxExpPerHour => Runs.Count > 0 ? Runs.Max(r => r.ExpPerHour) : 0;
    public double KillsPerHour => Runs.Count > 0 ? Runs.Average(r => r.KillsPerHour) : 0;
    public int Deaths => Runs.Count(r => r.DiedAtSeconds is not null);
    public int LowestHpPercent => Runs.Count > 0 ? Runs.Min(r => r.LowestHpPercent) : 100;
    public int LowestManaPercent => Runs.Count > 0 ? Runs.Min(r => r.LowestManaPercent) : 100;

    // Share of the simulated time in each activity, over every run.
    public double Share(Func<LoopSimRun, double> part)
    {
        double total = Runs.Sum(r => r.Seconds);
        return total > 0 ? Runs.Sum(part) / total : 0;
    }

    // Casts per hour by cast-code, over every run, most-cast first.
    public IReadOnlyList<(string Spell, double PerHour)> CastsPerHour()
    {
        double hours = Runs.Sum(r => r.RequestedHours);
        if (hours <= 0) return Array.Empty<(string, double)>();
        return Runs.SelectMany(r => r.Casts)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Sum(kv => kv.Value) / hours))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }
}
