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
//
// NothingLost says it was an arena death (a hang-up while dropped in an arena
// room): no life, item or coin was taken, so the record lists no pile and is
// written as a witnessed arena death's is.
//
// GameData is the set Room's number is of, as the held list recorded it; null from
// a list written before that was kept, and the record is then unstamped.
public sealed record UnwitnessedDeath(
    RoomRef? Room,
    DateTimeOffset At,
    int LivesRemaining,
    string Message,
    List<DeathItem>? Equipped,
    List<DeathItem>? Lost,
    CurrencyHoldings? Coins,
    bool AtEntry = true,
    bool NothingLost = false,
    string? GameData = null);
