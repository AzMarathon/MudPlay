namespace MudPlay.Game.Map;

// A floor item a Roomba sweep observed but did not move — either it matched no
// labeled room (and there was no catch-all), or a `get` for it failed because it
// was gone by sort time. Part of GhSweepReport. Runtime only — never persisted.
public sealed record GhSweepItemFound(RoomKey Room, string ItemName);
