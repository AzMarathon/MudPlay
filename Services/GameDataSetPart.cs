namespace MudPlay.Services;

// The kinds of user-made data kept for one game-data set, as Manage Game Data Sets
// offers them to copy or move into another set. The imported tables themselves are
// not a part: they come from the MDB. Favourites and triggers are the character's
// and the boss list is the realm's, so none of those is tied to a set.
[Flags]
public enum GameDataSetPart
{
    None              = 0,
    // The Loops folder: loops, Auto-Lair setups and the nav folders holding them.
    Loops             = 1 << 0,
    // Spell / condition messages, monster messages and the flavor prefixes.
    Messages          = 1 << 1,
    UnrecognizedLines = 1 << 2,
    // Game Data Browser edits: the `{table}_overrides.{set}.json` files beside each
    // tier (all characters, each realm, each character).
    RecordOverrides   = 1 << 3,
}
