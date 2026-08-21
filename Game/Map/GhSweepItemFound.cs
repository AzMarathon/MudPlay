namespace MudPlay.Game.Map;

// A floor item a Roomba sweep observed but did not move. Reason distinguishes the
// three causes (no matching room, gone by sort time, too heavy to carry). Part of
// GhSweepReport. Runtime only — never persisted.
public sealed record GhSweepItemFound(RoomKey Room, string ItemName, GhLeftReason Reason);
