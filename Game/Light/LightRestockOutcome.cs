namespace MudPlay.Game.Light;

// How a request for a light restock trip off a loop or Auto-Lair run went.
public enum LightRestockOutcome
{
    // The trip is under way: the engine is stopped and the walk to the shop began.
    Started,

    // Another trip has the walker right now; ask again later.
    Busy,

    // Nothing to do it with: no loop or lair running, no known room, or no shop
    // that sells the light within reach.
    NotPossible,
}
