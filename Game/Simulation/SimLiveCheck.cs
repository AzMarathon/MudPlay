namespace MudPlay.Game.Simulation;

// One loop the character played, set against the simulator's prediction for the
// same loop at the same level — how far to trust a simulated number. Problem says
// why there's no prediction (the loop isn't saved any more, the route no longer
// resolves).
public sealed record SimLiveCheckRow(LiveLoopRecord Live, LoopSimSummary? Simulated, string? Problem = null)
{
    public double? DiffPercent => Simulated is { ExpPerHour: > 0 } s && Live.ExpPerHour > 0
        ? (s.ExpPerHour / Live.ExpPerHour - 1) * 100 : null;

    public string Label
    {
        get
        {
            string live = $"{Live.Loop} L{Live.Level} · {Live.Hours:0.#} h live {Live.ExpPerHour:N0}/hr, " +
                          $"{Live.KillsPerHour:0} kills/hr, {Deaths(Live.Deaths)}";
            if (Problem is not null || Simulated is not { } s) return $"{live} — {Problem ?? "not simulated"}";
            string diff = DiffPercent is { } d ? $" ({d:+0.0;-0.0}%)" : "";
            return $"{live} → simulated {s.ExpPerHour:N0}/hr, {s.KillsPerHour:0} kills/hr, " +
                   $"died in {s.Deaths} of {s.Runs.Count} runs{diff}";
        }
    }

    private static string Deaths(int n) => n == 1 ? "1 death" : $"{n} deaths";
}
