namespace MudPlay.Game.Map;

// Outcome of a completed Roomba sweep (GhSweepManager). Runtime only — never
// persisted. Its element types live in their own files: GhSweepMove,
// GhSweepItemFound, GhSweepStranded.
public sealed record GhSweepReport(
    IReadOnlyList<GhSweepMove> Moved,
    IReadOnlyList<GhSweepItemFound> LeftInPlace,
    IReadOnlyList<GhSweepStranded> Stranded);
