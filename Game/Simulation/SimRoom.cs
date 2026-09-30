using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One room of a simulated lap, in walk order. NpcMonster is the room's placed
// fixture (0 = none) — back the moment you re-enter after killing it. LairMax is
// the lair's simultaneous spawn count; each of those slots carries its own respawn
// clock of RespawnSeconds from the kill and refills with one of LairMonsters.
// Bosses are kept apart: they're killable once per their long regen, which an
// hour's run can't sample, so they're credited at exp ÷ regen instead. Summon is
// the room's monster-summoning entry spell, if any; PauseSeconds is the delay of a
// waypoint command the loop runs here before walking on.
public sealed record SimRoom(
    RoomKey Key, int NpcMonster, int LairMax, IReadOnlyList<int> LairMonsters, int RespawnSeconds,
    IReadOnlyList<int>? Bosses = null, RoomSummonTable? Summon = null, double PauseSeconds = 0)
{
    public bool HasLair => LairMax > 0 && LairMonsters.Count > 0;
}
