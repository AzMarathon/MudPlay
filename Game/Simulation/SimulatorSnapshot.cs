namespace MudPlay.Game.Simulation;

// The Simulator window frozen for a bug report: the route picked, the last result
// (and the route and pace it was played at), the live check and the area ranking,
// each pre-formatted and null when not run.
public sealed record SimulatorSnapshot(
    string? Route,
    string RealmName,
    IReadOnlyList<string>? Simulation,
    double WalkSeconds,
    double Hours,
    int Runs,
    IReadOnlyList<string>? LiveCheck,
    IReadOnlyList<string>? AreaRanking,
    string? SimulatedRoute);
