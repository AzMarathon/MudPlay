using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One finished area ranking: the options best first, and what it ran with (the
// level, runs × hours, walk pace, the realm and the room it was ranked from), which
// a picked row's detail and a loop saved from it quote instead of settings changed
// since. Summary is the line shown above the list.
public sealed record AreaRanking(
    IReadOnlyList<AreaRank> Rows, int Level, double Step, double Hours, int Runs, RoomKey From, string Realm,
    string Summary);
