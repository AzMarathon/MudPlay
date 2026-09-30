namespace MudPlay.Game.Simulation;

// One loop the character played, set against the simulator's prediction for the
// same loop at the same level — how far to trust a simulated number. Problem says
// why there's no prediction (the loop isn't saved any more, the route no longer
// resolves).
public sealed record SimLiveCheckRow(LiveLoopRecord Live, LoopSimSummary? Simulated, string? Problem = null)
{
    public double? DiffPercent => Simulated is { ExpPerHour: > 0 } s && Live.ExpPerHour > 0
        ? (s.ExpPerHour / Live.ExpPerHour - 1) * 100 : null;

    public string Label => Problem is not null
        ? $"{Live.Loop} L{Live.Level} · {Live.Hours:0.#} h live {Live.ExpPerHour:N0}/hr — {Problem}"
        : $"{Live.Loop} L{Live.Level} · {Live.Hours:0.#} h live {Live.ExpPerHour:N0}/hr → simulated {Simulated!.ExpPerHour:N0} ({DiffPercent:+0.0;-0.0}%)";
}
