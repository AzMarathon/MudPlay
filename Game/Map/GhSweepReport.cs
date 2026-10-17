namespace MudPlay.Game.Map;

// Outcome of a Roomba sweep that ended (GhSweepManager). Runtime only — never
// persisted. Its element types live in their own files: GhSweepMove,
// GhSweepItemFound, GhSweepStranded.
//
// StoppedBecause: why it ended before it was done (a Stop, Reset States, its loop
// stopped or failed under it), or null when it finished.
public sealed record GhSweepReport(
    IReadOnlyList<GhSweepMove> Moved,
    IReadOnlyList<GhSweepItemFound> LeftInPlace,
    IReadOnlyList<GhSweepStranded> Stranded,
    string? StoppedBecause = null)
{
    public bool Finished => StoppedBecause is null;
}
