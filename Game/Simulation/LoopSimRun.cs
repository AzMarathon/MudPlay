namespace MudPlay.Game.Simulation;

// One simulated run of a loop: what the character earned and where the time went,
// the same measures Session Stats keeps live. A run that dies or hangs up stops
// there (DiedAtSeconds / HungUpAtSeconds) — nothing after it would count.
public sealed record LoopSimRun(
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
    IReadOnlyDictionary<string, int> Casts,
    int Flees = 0,
    double? HungUpAtSeconds = null,
    long DamageTaken = 0)
{
    public double Hours => Seconds / 3600.0;
    public double ExpPerHour => Hours > 0 ? Exp / Hours : 0;
    public double KillsPerHour => Hours > 0 ? Kills / Hours : 0;
    public double AvgLapSeconds => Laps > 0 ? Seconds / Laps : 0;
    public double DamageTakenPerHour => Hours > 0 ? DamageTaken / Hours : 0;
}

// A boss on the loop, credited at its exp ÷ regen hours rather than simulated.
public sealed record SimBossCredit(string Name, double ExpPerHour, double RegenHours);

// Several seeded runs of the same loop, summarised: the spread is the answer to
// "how much of that number is luck". Bosses' amortised exp rides on top of every
// run's figure.
public sealed record LoopSimSummary(IReadOnlyList<LoopSimRun> Runs, IReadOnlyList<SimBossCredit>? Bosses = null)
{
    public double BossExpPerHour => Bosses?.Sum(b => b.ExpPerHour) ?? 0;
    public double ExpPerHour => Runs.Count > 0 ? Runs.Average(r => r.ExpPerHour) + BossExpPerHour : 0;
    public double MinExpPerHour => Runs.Count > 0 ? Runs.Min(r => r.ExpPerHour) + BossExpPerHour : 0;
    public double MaxExpPerHour => Runs.Count > 0 ? Runs.Max(r => r.ExpPerHour) + BossExpPerHour : 0;
    public double KillsPerHour => Runs.Count > 0 ? Runs.Average(r => r.KillsPerHour) : 0;
    public int Deaths => Runs.Count(r => r.DiedAtSeconds is not null);
    public int HangUps => Runs.Count(r => r.HungUpAtSeconds is not null);
    public double DamageTakenPerHour => Runs.Count > 0 ? Runs.Average(r => r.DamageTakenPerHour) : 0;
    public double FleesPerHour => Runs.Sum(r => r.Hours) is > 0 and var h ? Runs.Sum(r => r.Flees) / h : 0;
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
        double hours = Runs.Sum(r => r.Hours);
        if (hours <= 0) return Array.Empty<(string, double)>();
        return Runs.SelectMany(r => r.Casts)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Sum(kv => kv.Value) / hours))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }
}
