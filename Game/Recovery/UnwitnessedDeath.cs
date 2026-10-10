using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Recovery;

// A death the client did not see happen, with what its record needs: where and
// when the character was last seen, its lives afterwards, the line the record
// shows in place of a death message, and the pile (the worn pieces with their
// slots, the rest by name, the coins). RoomTracker.NoteUnwitnessedDeath turns it
// into the same DeathRecord a death line makes.
public sealed record UnwitnessedDeath(
    RoomRef? Room,
    DateTimeOffset At,
    int LivesRemaining,
    string Message,
    List<DeathItem> Equipped,
    List<DeathItem> Lost,
    CurrencyHoldings? Coins);
