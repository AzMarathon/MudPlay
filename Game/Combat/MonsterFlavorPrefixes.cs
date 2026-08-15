namespace MudPlay.Game.Combat;

// The shared vocabulary of monster flavor adjectives — the size / mood words the game
// prepends to a monster's base name ("large giant rat", "nasty kobold"). It's a small,
// fixed set at the game-engine level, NOT per-monster data: these are the 17 distinct
// prefixes across all 1100 stock monster records, so ANY monster can carry ANY of them.
// Using one global list lets the room classifier resolve a prefixed name generically —
// a custom game needs no per-monster FlavorPrefixes for the standard adjectives.
//
// Collision note: 22 canonical monster names START with one of these words ("huge
// basilisk", "large yeti", "adult red dragon", "Great Hydra"). So a prefix strip that
// uses this set MUST run only AFTER the canonical full-name match, or it would reduce
// "huge basilisk" to "basilisk". RoomEntityClassifier honours that ordering.
public static class MonsterFlavorPrefixes
{
    public static readonly IReadOnlySet<string> Global = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // common (applied broadly across the roster)
        "large", "big", "small", "nasty", "fierce", "fat", "thin", "angry", "tall", "short", "happy",
        // rare (a handful of monsters each)
        "adult", "massive", "colossal", "huge", "enormous", "great",
    };

    // True when word is a known flavor adjective (case-insensitive).
    public static bool IsPrefix(string word) => Global.Contains(word);
}
