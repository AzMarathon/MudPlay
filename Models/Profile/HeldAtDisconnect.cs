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
// drops, the game is left or the app closes is the one that stands. A death
// clears it until the next inventory read: the record in memory still names
// what went into the deathpile. Coins are not on it: the penalty takes items.
public sealed class HeldAtDisconnect
{
    public DateTimeOffset At { get; set; }

    // The BBS and realm the list was taken on ("bbs/realm"). A list from another
    // one says nothing about this character's pack here, and is ignored.
    public string? Realm { get; set; }

    // The room the character stood in, or null when the map wasn't sure of it.
    public RoomRef? Room { get; set; }

    // How many drops of the link the list covers: 1, or more when a check was cut
    // short by the link dropping again and what it hadn't found was kept on the
    // list. Each one may have cost the realm's item count, so the cap on what a
    // check takes back is that count times this.
    public int PenaltiesSpanned { get; set; } = 1;

    public List<HeldItem> Items { get; set; } = new();
}
