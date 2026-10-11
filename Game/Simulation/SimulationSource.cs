using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// What a view needs to simulate the live character: Build assembles the character
// (at an optional level) and game data, null before the first `stat`; Character
// names whose logged sessions are theirs and Level is where they stand now;
// WalkSeconds is their seconds per room with the given lag; Room is where they
// stand (null until the room tracker knows); LogsDir is where the program logs
// the live check reads back live.
//
// For a ranking kept between sessions: World is the game data as it is now (null
// with no set active), and CharacterAt the character as it would be simulated at a
// level, as the groups a kept ranking is compared by (null while it can't be read:
// no `stat` yet, or no inventory, without which the gear is unknown).
public sealed record SimulationSource(
    Func<int?, (SimCharacter Character, SimWorld World)?> Build,
    Func<string?> Character,
    Func<int> Level,
    Func<double, double> WalkSeconds,
    Func<RoomKey?> Room,
    string LogsDir,
    Func<RankingWorld?> World,
    Func<int, RankingCharacter?> CharacterAt);
