using System.Text.RegularExpressions;

namespace MudPlay.Services.Patterns;

// A monster's record names a movement message, and the engine prints that message's
// first line to the room when it brings the monster into it (GAME_MECHANICS
// "Monster movement lines"). The imported game data has no column for it, so the
// lines are carried here: every appear line of the Stock 1.11p message table that a
// monster record points at and that neither RoomEntryArrival nor RoomSpawnArrival's
// own phrases read, with its message number. Paradigm prints the same messages for
// the monsters it shares with Stock; a monster Paradigm added may have a line of its
// own that isn't here.
//
// MonsterName is set when every monster that prints the line shares one name and the
// line doesn't stand in a random name for it, so the arrival can go on the roster
// without waiting for the room. "%s" is where the engine puts a direction or a
// random name: it matches any text.
public static class MonsterAppearLines
{
    public sealed record Entry(int MessageNumber, string Text, string? MonsterName)
    {
        internal Regex Regex { get; } = new(
            "^" + ToPattern(Text) + @"\s*$", RegexOptions.CultureInvariant);
    }

    public static readonly IReadOnlyList<Entry> All =
    [
        new(244,  "A banshee slowly appears from the ether!", "banshee"),
        new(1036, "A swirling red mist forms nearby!", "crimson mist"),
        new(1072, "The hydra strains against its icy bond, breaks it, and comes to life!", "frost hydra"),
        new(1104, "An unholy idol animates!", "unholy idol"),
        new(1193, "A dark storm approaches!", null),                      // planar storm, random name
        new(1331, "The slaver leader whimpers and drops his weapon, calling for mercy!", "dying slaver leader"),
        new(1388, "The statue animates, and attacks!", "obsidian statue"),
        new(2030, "A %s suddenly forms beside you!", null),               // dustdevil, random name
        new(2050, "A serpentine tentacle bursts through the wall!", "spiny tentacle"),
        new(2189, "The storm giant king stands here in his throne room.", "storm giant king"),
        new(2281, "Hundreds of insects rise from the ground forming a large swarm.", null), // insect / mosquito swarm
        new(2285, "A whipvine breaks through the ground.", "whipvine"),  // also message 2318
        new(2314, "The feline form of a grall emerges from the shadows.", "grall"),
        new(2316, "A moss zombie rises from the ground!", "moss zombie"),
        new(2369, "The king crab burrows up through the sand.", "king crab"),
        new(2399, "The dark phoenix hatches from its volcanic nest!", "dark phoenix"),
        new(2477, "A %s swoops down upon you!", null),                    // pyrimera, random name
        new(2656, "The giant shrugs off his glacial prison, and bellows in rage!", "frost giant"),
        new(2743, "Out of nothingness a hundred scarab beetles appear to feast on you.", "scarab swarm"),
        new(2758, "A deathly shriek announces an evil prescence!", null), // Death Shrieker, death chicken
        new(2820, "The %s appears in a flash of bright light!", "Ghost of Justice Darkbane"),
        new(2890, "A massive wave appears from %s, announcing a massive sea creature!", "tsunami squid"),
        new(2932, "A golem stomps in from %s with a thundering crash.", null), // flesh / ivory golem
        new(3032, "A yellow cloud billows through the room!", "groaning cloud"),
        new(3038, "A farm animal stumbles in from %s", null),            // famished cow, starving pig
        new(3090, "A mocking female laugh announces an evil prescence!", null), // Demoness Irikani, Vashti, Vasili
        new(3145, "The aura of death enters the room!", "Zanthus the Lich"),
        new(3277, "A mist of gold appears in front of you!", "golden mist"),
        new(3347, "A laughter filled with ancient evil sounds behind you!", "Nahr"),
        new(3352, "A hovering sphere appears out of thin air!", null),    // red / blue / green / yellow sphere
        new(3380, "A head sprouts from the hydra's shoulders!", null),    // hydra head, random name
        new(8421, "A moaning spirit appears right beside you!", "moaning spirit"),
    ];

    // The lines as one regex alternation, anchored, for RoomSpawnArrival.
    public static string Alternation { get; } =
        @"^(?:" + string.Join("|", All.Select(e => ToPattern(e.Text))) + @")\s*$";

    private static string ToPattern(string text)
        => Regex.Escape(text).Replace("%s", ".+?", StringComparison.Ordinal);

    public static Entry? Find(string line)
    {
        foreach (Entry entry in All)
            if (entry.Regex.IsMatch(line)) return entry;
        return null;
    }
}
