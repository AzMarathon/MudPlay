namespace MudPlay.Game.Map;

// One leg of a loop, waypoint to waypoint, as LoopExpander plans it: its steps and
// lever detours (indices into its own steps), or Reaches false with the lever
// rooms it couldn't reach.
internal sealed record LoopLeg(
    bool Reaches,
    IReadOnlyList<LoopStep> Steps,
    IReadOnlyList<LeverDetour> Detours,
    IReadOnlyList<UnroutableLeverLeg> Unroutable)
{
    public static readonly LoopLeg NoRoute =
        new(false, Array.Empty<LoopStep>(), Array.Empty<LeverDetour>(), Array.Empty<UnroutableLeverLeg>());
}
