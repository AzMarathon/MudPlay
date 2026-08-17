namespace MudPlay.Game.Map;

// Outcome of a completed Roomba sweep (GhSweepManager). Runtime only — never
// persisted.
public sealed record GhSweepItemFound(RoomKey Room, string ItemName);

public sealed record GhSweepMove(RoomKey From, RoomKey To, string ItemName);

// An item that was picked up mid-sweep and never delivered — either the sweep
// was interrupted (LoopRunner Stopped/Failed externally) while it was in the
// player's pack, or the sort phase stalled with it still carried. Distinct
// from LeftInPlace (which never left the floor): a Stranded item is sitting in
// the player's inventory right now and needs a manual drop at IntendedDestination.
public sealed record GhSweepStranded(RoomKey CarriedFrom, RoomKey IntendedDestination, string ItemName);

public sealed record GhSweepReport(
    IReadOnlyList<GhSweepMove> Moved,
    IReadOnlyList<GhSweepItemFound> LeftInPlace,
    IReadOnlyList<GhSweepStranded> Stranded);
