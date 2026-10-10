namespace MudPlay.Game.Map;

// A summon line the table names and the estimate doesn't count, kept so the log can
// say what was left out and why.
public readonly record struct RoomSummonLeftOut(int Threshold, IReadOnlyList<int> Monsters, string Reason);
