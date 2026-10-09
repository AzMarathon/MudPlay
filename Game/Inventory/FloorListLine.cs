namespace MudPlay.Game.Inventory;

// The room's floor list, "You notice <list> here.", when the game wraps it over
// several rows. Other sentences open with the same words and are not it. A look at a
// corpse prints "You notice the following on the corpse:" and then what the corpse
// holds, coins first; read as the floor list it sent a get for coins that aren't on
// the floor.
public static class FloorListLine
{
    private const string Prefix = "You notice ";
    private const string Suffix = " here.";

    // A row that opens a floor list still to be finished on later rows. A row ending in
    // a colon heads some other list, and one ending in a full stop is whole already.
    public static bool OpensWrappedList(string row) =>
        row.StartsWith(Prefix, StringComparison.Ordinal)
        && !row.EndsWith('.') && !row.EndsWith(':');

    // The list inside a finished floor line, or false for any other sentence.
    public static bool TryReadList(string completeLine, out string list)
    {
        list = string.Empty;
        string line = completeLine.TrimEnd();
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)
            || !line.EndsWith(Suffix, StringComparison.Ordinal)
            || line.Length <= Prefix.Length + Suffix.Length)
            return false;
        list = line[Prefix.Length..^Suffix.Length];
        return true;
    }
}
