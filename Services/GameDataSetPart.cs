namespace MudPlay.Services;

// The kinds of user-made data kept for one game-data set, as Manage Game Data Sets
// offers them to copy or move into another set. The imported tables themselves are
// not a part: they come from the MDB.
[Flags]
public enum GameDataSetPart
{
    None              = 0,
    // The Loops folder: loops, Auto-Lair setups and the nav folders holding them.
    Loops             = 1 << 0,
    Favorites         = 1 << 1,
    // The user's boss list (bosses.json). Kill timers are kept per realm, not here.
    Bosses            = 1 << 2,
    // Triggers saved to the game data rather than to a character.
    Triggers          = 1 << 3,
    // Spell / condition messages, monster messages and the flavor prefixes.
    Messages          = 1 << 4,
    UnrecognizedLines = 1 << 5,
    // Game Data Browser edits: the `{table}_overrides.{set}.json` files beside each
    // tier (all characters, each realm, each character).
    RecordOverrides   = 1 << 6,
}
