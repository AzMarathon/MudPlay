namespace MudPlay.Game.Simulation;

// The world a ranking was made in: what it depends on that is not the character.
// A ranking kept on disk is listed again only while all of it is unchanged. One
// made in another world is not a ranking of this one in an earlier state: it is
// about different rooms, monsters or rules, and is dropped, not marked.
//
//   SimulatorVersion  LoopSimulator.SimulatorVersion when it was made.
//   Tables            the imported tables a ranking is built from, each by its
//                     file's length and last-write time: Rooms (the map and its
//                     lairs), Monsters, Spells, Items, TBInfo (room-spell summon
//                     tables), Lairs (respawn times), Classes and Races (what the
//                     character can do at a level). Shops and Info are not read.
//   Seed              the monster seed that files monsters under areas and says
//                     which are fought, by content: an update replaces it at launch
//                     with no event.
public sealed record RankingWorld(int SimulatorVersion, IReadOnlyDictionary<string, string> Tables, string Seed)
{
    // The tables a ranking reads, as GameDataCache names them.
    public static readonly IReadOnlyList<string> TableNames =
        new[] { "Rooms", "Monsters", "Spells", "Items", "TBInfo", "Lairs", "Classes", "Races" };

    // Why a ranking made in made is not about this world, in words for the program
    // log, or null when it is.
    public string? DiffersFrom(RankingWorld made)
    {
        ArgumentNullException.ThrowIfNull(made);
        if (made.SimulatorVersion != SimulatorVersion)
            return $"it was made by simulator version {made.SimulatorVersion} and this is version {SimulatorVersion}";
        List<string> changed = TableNames
            .Where(t => !string.Equals(made.Tables.GetValueOrDefault(t), Tables.GetValueOrDefault(t), StringComparison.Ordinal))
            .ToList();
        if (changed.Count > 0)
            return $"the game data's {string.Join(", ", changed)} table{(changed.Count == 1 ? " has" : "s have")} changed since";
        if (!string.Equals(made.Seed, Seed, StringComparison.Ordinal))
            return "the monster seed (area labels and which monsters are fought) has changed since";
        return null;
    }
}
