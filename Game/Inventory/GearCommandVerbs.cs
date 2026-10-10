namespace MudPlay.Game.Inventory;

// The command words that put gear on or take it off, each down to the shortest form
// the game takes: `eq`…`equip`, `wea` / `wear`, `wield`, `rem`…`remove` (GAME_MECHANICS
// "Equip / remove verbs" and "Command words and abbreviations").
public static class GearCommandVerbs
{
    private static readonly (string Verb, int Shortest)[] Verbs =
    {
        ("remove", 3), ("equip", 2), ("wear", 3), ("wield", 5),
    };

    // word is the first word of a command, any case.
    public static bool IsGearVerb(string? word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        foreach ((string verb, int shortest) in Verbs)
            if (word.Length >= shortest && verb.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
