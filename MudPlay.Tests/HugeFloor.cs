using System.Text;

namespace MudPlay.Tests;

// Builds the floor of a room with no item cap: a Paradigm vault holds hundreds of
// stacks, and a search prints them all as one unbroken "You notice … here." line
// (report paradigm-20261009-164508: 116 stacks in ~2,200 characters). The names are
// made up from two word lists so a test can ask for thousands of distinct items and
// write a matching Items table, without leaning on anyone's game data.
internal static class HugeFloor
{
    private static readonly string[] Adjectives =
    {
        "antlered", "petrified", "thundering", "feathered", "carved", "adamantite", "dull", "ivory",
        "engraved", "golden", "serrated", "crimson", "trollskin", "woven", "jagged", "gilded",
        "glowing", "elven", "vorpal", "runic", "moldy", "clawed", "greasy", "martial", "chromatic",
        "ornate", "ruby-eyed", "holy", "shinobi", "black", "starsteel", "heavy", "macabre", "astral",
        "scarlet", "nightblack", "gleaming", "amber", "celestial", "ancient", "grimy", "wicked",
        "silken", "opaline", "midnight", "oily", "tiger-eye", "gossamer", "turquoise", "jeweled",
        "high-necked", "prismatic", "ethereal", "lunar", "battered", "arcane", "runed", "rotted",
        "lost", "shining", "dusty", "shamanic", "bubbling", "emblazoned", "imbued", "beaded",
        "glyphic", "frothing", "crumbling", "worn",
    };

    private static readonly string[] Nouns =
    {
        "helm", "corselet", "scroll", "regalia", "mask", "bracers", "ring", "warhorn", "chalice",
        "shield", "gauntlets", "boots", "crown", "key", "robes", "broadsword", "staff", "falchion",
        "earring", "rod", "armwraps", "gloves", "sash", "spear", "sleeves", "manacle", "necklace",
        "whip", "chainmail", "medallion", "blade", "belt", "doublet", "javelin", "greaves", "vial",
        "sigil", "legguards", "femur", "pendant", "cane", "talisman", "relic", "hood", "breeches",
        "glaive", "sphere", "eyepatch", "bracelet", "sickle", "crystal", "potion", "raft", "amulet",
        "sandals", "pantaloons", "epee", "armour", "cross", "dagger", "turban", "tunic", "claymore",
        "sabre", "hat", "coif", "chakram", "maul", "warhammer", "wand", "parchment", "tome",
    };

    public static int MaxDistinct => Adjectives.Length * Nouns.Length;

    public static string Name(int index)
    {
        if (index < 0 || index >= MaxDistinct) throw new ArgumentOutOfRangeException(nameof(index));
        return $"{Adjectives[index % Adjectives.Length]} {Nouns[index / Adjectives.Length]}";
    }

    // How many lie on the floor for item index: every third stack holds several.
    public static int CountOf(int index) => index % 3 == 0 ? 2 + index % 9 : 1;

    // The stacks first..first+stacks-1 as the game words them: a lone item by name,
    // a stack with its count in front.
    public static IReadOnlyList<string> Entries(int stacks, int first = 0)
    {
        List<string> entries = new(stacks);
        for (int i = first; i < first + stacks; i++)
            entries.Add(CountOf(i) > 1 ? $"{CountOf(i)} {Name(i)}" : Name(i));
        return entries;
    }

    public static string Line(int stacks, int first = 0) =>
        $"You notice {string.Join(", ", Entries(stacks, first))} here.";

    // An Items table holding the first count names: even indexes are weapons
    // (ItemType 1), odd ones armour (ItemType 0).
    public static string ItemsJson(int count)
    {
        StringBuilder json = new("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($"{{ \"Number\": {i + 1}, \"Name\": \"{Name(i)}\", \"ItemType\": {(i % 2 == 0 ? 1 : 0)}, \"Encum\": 1 }}");
        }
        return json.Append(']').ToString();
    }
}
