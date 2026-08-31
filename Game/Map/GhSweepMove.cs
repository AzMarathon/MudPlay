namespace MudPlay.Game.Map;

// A completed relocation during a Roomba sweep — an item picked up at From and
// verified dropped at its labeled destination To. Part of GhSweepReport. Runtime
// only — never persisted.
public sealed record GhSweepMove(RoomKey From, RoomKey To, string ItemName, int Count);
