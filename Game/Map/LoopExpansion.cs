namespace MudPlay.Game.Map;

// A loop's cycle as LoopExpander.Plan works it out: the steps the runner takes,
// the legs that don't route, each lever detour on the lap (step indices into
// Steps), and each lever room a detour couldn't reach (whose leg is among the
// unreachable ones).
public sealed record LoopExpansion(
    IReadOnlyList<LoopStep> Steps,
    IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments,
    IReadOnlyList<LeverDetour> Detours,
    IReadOnlyList<UnroutableLeverLeg> UnroutableLevers)
{
    public static readonly LoopExpansion Empty = new(
        Array.Empty<LoopStep>(), Array.Empty<(RoomKey, RoomKey)>(), Array.Empty<LeverDetour>(),
        Array.Empty<UnroutableLeverLeg>());
}
