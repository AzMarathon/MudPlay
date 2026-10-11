using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// What a view needs to simulate the live character: Build assembles the character
// (at an optional level) and game data, null before the first `stat`; Character
// names whose logged sessions are theirs and Level is where they stand now;
// WalkSeconds is their seconds per room with the given lag; Room is where they
// stand (null until the room tracker knows); LogsDir is where the program logs
// the live check reads back live.
//
// For a ranking kept between sessions: World is the game data this client is running
// on (null with no set active, or when the tables in memory are not of one version
// of the set: RankingWorldWatch), and CharacterAt the character as it would be
// simulated at a level, as the groups a kept ranking is compared by (null while it
// can't be read: no `stat` or no inventory on this connection). Load says which load
// of the game data is in hand, equal for as long as it is the same one: a ranking
// is stamped with World when it finishes, by which time it has read every table it
// uses, and only if the load is still the one it started in.
public sealed record SimulationSource(
    Func<int?, (SimCharacter Character, SimWorld World)?> Build,
    Func<string?> Character,
    Func<int> Level,
    Func<double, double> WalkSeconds,
    Func<RoomKey?> Room,
    string LogsDir,
    Func<RankingWorld?> World,
    Func<int, RankingCharacter?> CharacterAt,
    Func<object?> Load);
