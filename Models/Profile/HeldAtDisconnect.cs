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
// what went into the deathpile.
//
// It also carries what tells a death nobody saw from an ordinary reconnect: a
// hang-up while dropped or low on HP, on a board that penalises it, can kill the
// character after the link is gone (HP at the drop, the fight it was in, its
// lives), and what a death record then needs that the item list alone doesn't
// have (what was worn and in which slot, and the coins on hand).
public sealed class HeldAtDisconnect
{
    public DateTimeOffset At { get; set; }

    // The BBS and realm the list was taken on ("bbs/realm"). A list from another
    // one says nothing about this character's pack here, and is ignored.
    public string? Realm { get; set; }

    // The character's name as a `stat` on that connection gave it, or null when
    // none was read. A profile can be loaded over another character (a copied
    // profile, a shared account): a list of one says nothing about the other.
    public string? Character { get; set; }

    // The room the character stood in, or null when the map wasn't sure of it.
    public RoomRef? Room { get; set; }

    // How many drops of the link the list covers: 1, or more when a check was cut
    // short by the link dropping again and what it hadn't found was kept on the
    // list. Each one may have cost the realm's item count, so the cap on what a
    // check takes back is that count times this.
    public int PenaltiesSpanned { get; set; } = 1;

    // Worn, carried, lit and key-ring items by name and count. Coins are not on
    // it: the penalty takes items.
    public List<HeldItem> Items { get; set; } = new();

    // HP and max HP as the statline last showed them, or null when they weren't
    // known. At or below 0 the character was dropped.
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }

    // Lives as the game gave them on that connection (a `stat`, or the readout of
    // a death that was seen), or null when it gave none: a count carried over from
    // an earlier session can be a death behind.
    public int? Lives { get; set; }

    // A fight with a player was under way, or a player had just attacked.
    public bool PvpFight { get; set; }

    // In combat with a monster, or a hostile one in the room.
    public bool InCombat { get; set; }

    // The worn pieces among Items, each with its slot, so a death record made from
    // this list can put them back on after the pile is recovered. null on a list
    // written before this was kept.
    public List<DeathItem>? Worn { get; set; }

    // The carried entries among Items as the inventory list words them ("3 torch"),
    // without the keys and the lit light, which a death record's pile doesn't
    // carry either. null on a list written before this was kept.
    public List<DeathItem>? Carried { get; set; }

    // The coins on hand: a death takes them too.
    public Game.Inventory.CurrencyHoldings? Coins { get; set; }
}
