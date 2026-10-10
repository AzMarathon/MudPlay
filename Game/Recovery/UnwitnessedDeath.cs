using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Recovery;

// A death the client did not see happen, with what its record needs: where and
// when the character was last seen, its lives afterwards, the line the record
// shows in place of a death message, and the pile (the worn pieces with their
// slots, the rest by name, the coins; null where what it held isn't known).
// RoomTracker.NoteUnwitnessedDeath turns it into the same DeathRecord a death
// line makes.
//
// AtEntry says the death was worked out while movement was still held at the
// login, with nothing having run since the connect. Only then does it stop the
// engines and clear the buff timers as a death does: a verdict that arrives
// later (a `stat` typed hours in) finds a character that has been playing since,
// with buffs cast after the death and a loop that is doing fine.
public sealed record UnwitnessedDeath(
    RoomRef? Room,
    DateTimeOffset At,
    int LivesRemaining,
    string Message,
    List<DeathItem>? Equipped,
    List<DeathItem>? Lost,
    CurrencyHoldings? Coins,
    bool AtEntry = true);
