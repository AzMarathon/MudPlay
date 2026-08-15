namespace MudPlay.Game.Combat;

// The built-in vocabulary of monster flavor adjectives — the size / mood words the game
// prepends to a monster's base name ("large giant rat", "nasty kobold"). It's a small,
// fixed set at the game-engine level, NOT per-monster data: these are the 17 distinct
// prefixes across all 1100 stock monster records, so ANY monster can carry ANY of them.
// One global list lets the room classifier resolve a prefixed name generically.
//
// This is only the DEFAULT seed. The live, editable vocabulary is Services.FlavorPrefixStore,
// which is per-game-data-set (a different door game uses different adjectives) and starts
// from this list — the user adds/removes words for a custom realm in the Game Data Browser's
// Flavor Prefixes section. RoomEntityClassifier reads the store, not this constant.
//
// Collision note: 22 canonical monster names START with one of these words ("huge
// basilisk", "large yeti", "adult red dragon", "Great Hydra"). So a prefix strip that
// uses this set MUST run only AFTER the canonical full-name match, or it would reduce
// "huge basilisk" to "basilisk". RoomEntityClassifier honours that ordering.
public static class MonsterFlavorPrefixes
{
    public static readonly IReadOnlyList<string> DefaultPrefixes = new[]
    {
        // common (applied broadly across the roster)
        "large", "big", "small", "nasty", "fierce", "fat", "thin", "angry", "tall", "short", "happy",
        // rare (a handful of monsters each)
        "adult", "massive", "colossal", "huge", "enormous", "great",
    };
}
