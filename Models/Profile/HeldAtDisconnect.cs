namespace MudPlay.Models.Profile;

// What the character held the last time it was known to be in the game, kept so
// the next entry can tell what a penalised hang-up dropped (GAME_MECHANICS
// "Hang-up / lost carrier"; Game.Inventory.HangupItemRecheck). The client's own
// inventory record lives in memory and is rebuilt by the login's `i`, which by
// then lists what is left, so the list from before has to be on disk to outlast
// a dropped link, an app restart or an update restart.
//
// Written on every profile save made while the character is in the game and an
// inventory has been read on that connection, so the last save before the link
// drops, the game is left or the app closes is the one that stands. Coins are
// not on it: the penalty takes items.
public sealed class HeldAtDisconnect
{
    public DateTimeOffset At { get; set; }

    // The room the character stood in, or null when the map wasn't sure of it.
    public RoomRef? Room { get; set; }

    public List<HeldItem> Items { get; set; } = new();
}
