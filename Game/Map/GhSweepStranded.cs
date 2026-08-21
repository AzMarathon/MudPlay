namespace MudPlay.Game.Map;

// An item that was picked up mid-sweep and never delivered — either the sweep was
// interrupted (LoopRunner Stopped/Failed externally) while it was in the player's
// pack, or the sort phase stalled with it still carried. Distinct from
// GhSweepItemFound (which never left the floor): a Stranded item is sitting in the
// player's inventory right now and needs a manual drop at IntendedDestination.
// Part of GhSweepReport. Runtime only — never persisted.
public sealed record GhSweepStranded(RoomKey CarriedFrom, RoomKey IntendedDestination, string ItemName);
