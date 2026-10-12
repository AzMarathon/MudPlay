using MudPlay.Services;

namespace MudPlay.Game.Map;

// What became of the game data between two loads of it, for whatever goes by room
// numbers: nothing; another set became the active one, under which the numbers are
// other rooms; or the active set was read in again (imported again, rewritten in
// Manage Sets, picked again from the Game Data menu), after which they may be.
// Told by the cache's load, which moves on both and on nothing else: a SwitchSet to
// the name already active is answered without a new load, and is no change.
public readonly record struct GameDataLoadChange(GameDataLoadChange.Kind What, string? Was, string? Now)
{
    public enum Kind { Nothing, AnotherSet, ReadInAgain }

    public static GameDataLoadChange Between(GameDataCache.Load was, GameDataCache.Load now) =>
        was == now
            ? default
            : new(string.Equals(was.Set, now.Set, StringComparison.OrdinalIgnoreCase) ? Kind.ReadInAgain : Kind.AnotherSet,
                was.Set, now.Set);

    public bool Changed => What != Kind.Nothing;

    public bool ReadInAgain => What == Kind.ReadInAgain;

    // The change as it reads after the colon of a terminal notice.
    public string Why => ReadInAgain
        ? $"game data '{Now ?? "(none)"}' was read in again"
        : $"the game data changed from '{Was ?? "(none)"}' to '{Now ?? "(none)"}'";
}
