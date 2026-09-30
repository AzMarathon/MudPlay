namespace MudPlay.Game.Simulation;

// What a view needs to simulate the live character: Build assembles the character
// (at an optional level) and game data, null before the first `stat`; Character
// names whose logged sessions are theirs and Level is where they stand now;
// WalkSeconds is their seconds per room with the given lag; LogsDir is where the
// program logs the live check reads back live.
public sealed record SimulationSource(
    Func<int?, (SimCharacter Character, SimWorld World)?> Build,
    Func<string?> Character,
    Func<int> Level,
    Func<double, double> WalkSeconds,
    string LogsDir);
