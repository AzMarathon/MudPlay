namespace MudPlay.Game.Simulation;

// The Simulator window frozen for a bug report: the route picked, the last result
// (and the route and pace it was played at), the live check and the area ranking,
// each pre-formatted and null when not run. ForSet and ForCharacter are the game
// data and character everything listed was made on; RankingsKept counts the
// rankings held in memory for every pairing, the one listed included.
// ListedRanking says where the ranking on show came from (just made, kept, read
// back from disk), how old it is and what it is marked for; RankingFiles is what
// the files on disk have lately been through (saved, read back, dropped and why).
public sealed record SimulatorSnapshot(
    string? Route,
    string RealmName,
    IReadOnlyList<string>? Simulation,
    double WalkSeconds,
    double Hours,
    int Runs,
    IReadOnlyList<string>? LiveCheck,
    IReadOnlyList<string>? AreaRanking,
    string? SimulatedRoute,
    string? ForSet,
    string? ForCharacter,
    int RankingsKept,
    string? ListedRanking,
    IReadOnlyList<string> RankingFiles);
