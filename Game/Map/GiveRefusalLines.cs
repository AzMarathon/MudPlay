namespace MudPlay.Game.Map;

// What an NPC or room says when it won't hand an item over: the line the game
// prints when a condition on a give fails, by message number. An award line names
// the number ("goodaligned -51 3075"); the imported game data carries no message
// table, so the wording is listed here, read from the Stock 1.11p table for every
// number a give line in either realm's data refers to. A number Paradigm reworded
// simply never matches, and the give then reads as unanswered instead of refused.
internal static class GiveRefusalLines
{
    // The refusal line for a message number, or null when it isn't one we know.
    public static string? For(int messageNumber)
        => Lines.TryGetValue(messageNumber, out string? line) ? line : null;

    private static readonly Dictionary<int, string> Lines = new()
    {
        [801] = "You are not Pure enough to that!",
        [839] = "The Grey Lord simply stares at you in silence.",
        [840] = "Commander Markus frowns at you, and waves you away.",
        [841] = "Chancellor Annora ignores you.",
        [850] = "The huge dwarven witchunter screams, \"Ye already got yer weapon, maggot!\"",
        [851] = "The huge dwarven witchunter shouts, \"Ye are too unworthy, ye pansy!\"",
        [865] = "Balthazar laughs, \"Begone, fool. I have no use for the good-hearted.\"",
        [866] = "Balthazar growls, \"You already serve another master, idiot.\"",
        [867] = "Balthazar shouts, \"You bumbling idiot! Where is the head of Markus!\"",
        [868] = "Balthazar screams, \"You fool! I need what he carried as well as his head!\"",
        [1094] = "You do not have that.",
        [1114] = "Nothing happens.",
        [1182] = "You do not have that item.",
        [1368] = "When the brooch is not forthcoming, he spits in disgust and turns away!",
        [1369] = "The shopkeeper frowns in anger when you say you do not have the orb.",
        [1437] = "A suit of armour!",
        [1608] = "You do not have a pickaxe to mine with.",
        [1684] = "You do not have an obsidian talisman.",
        [1710] = "When you don't show him anything, he snorts and goes back to work.",
        [1850] = "With a tone of annoyance, he says, \"I am not finished. Leave me be.\"",
        [2010] = "The master smith waves you off in disgust when you give him nothing.",
        [2048] = "You do not find anything of note.",
        [2498] = "The gnome merchant says, \"Come back when ye have what I need.\"",
        [2499] = "He says, \"Curse you! If you don't have his head, be gone!\"",
        [2507] = "The gaunt elder shakes their head mutely, you do not have the item.",
        [2614] = "Dhelvanen shakes his head. \"This is not what I requested.\"",
        [2936] = "The old man looks incredibly angry and hisses \"This is not what I want.\"",
        [2954] = "The ghost shakes his head, \"Alas thou art not pure.\"",
        [2955] = "The ghost shakes his head \"Alas thou hath not the strength\"",
        [2996] = "The pastor shakes his head, \"Don't waste an old man's time.\"",
        [3020] = "He shakes his head, \"Do not try to fool me.\"",
        [3075] = "The hermit sneers at you, \"You can't serve your cause like that!\"",
        [3121] = "He shakes his head at you, \"Stop playing tricks on an old man!\"",
        [3154] = "A strange power holds you back!",
        [3208] = "You are embarassed to find you do not seem to have the locked wooden box.",
        [3244] = "Balthazar laughs at you, \"I have work but not for one as puny as you!\"",
        [3245] = "The Grey Lord shakes his head, \"You must first grow further, young one.\"",
        [3285] = "The imp spits at you furiously, \"No key for you if no spikes I reap!\"",
        [3297] = "The imp scowls, \"Do not play games mortal...\"",
        [3464] = "The witchunter looks at you, and decides it better to keep to himself.",
    };
}
