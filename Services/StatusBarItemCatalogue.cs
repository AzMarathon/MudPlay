using System.Text;

namespace MudPlay.Services;

// How a status-bar item draws. Every kind also has a plain-text reading, used in a
// marquee row and inside a custom text.
public enum StatusBarItemKind
{
    Text,
    EngineChip,
    StatlineWarning,
    ConnectionLight,
    CustomText,
}

// One thing the status bar can show. Sample is what the Settings preview shows when
// the live value is empty (nothing loaded, not connected).
public sealed record StatusBarItemDef(
    string Id, string Group, string Label, string Description, string Sample,
    StatusBarItemKind Kind = StatusBarItemKind.Text, bool Mono = false);

// Everything the status bar can show, in the order the Settings picker lists it.
// Ids are saved in the global settings and typed into custom texts — never rename
// one.
public static class StatusBarItemCatalogue
{
    public const string CustomTextId = "text";

    private const string Bar = "Standard bar", Who = "Character", Vitals = "Vitals",
        Where = "Location and movement", Fight = "Combat and session", Other = "Other";

    public static IReadOnlyList<StatusBarItemDef> All { get; } = new StatusBarItemDef[]
    {
        new("engine", Bar, "Engine state chip", "IDLE / WALK / LOOP / LAIR, coloured, with the recovery-tier border.", "LOOP", StatusBarItemKind.EngineChip),
        new("location", Bar, "Location and exp rate", "Map/room and exp per hour; the walk readout during a walk-to; the lap during a loop.", "1/297 · 1.2M/hr"),
        new("target", Bar, "Looked-at target HP", "The HP range of the monster you last looked at. Empty until you look.", "TGT HP: wounded [~340]", Mono: true),
        new("statline", Bar, "Statline warning", "Shows only while the game's prompt doesn't match Settings → Statline. Click opens that tab.", "STATLINE MISMATCH", StatusBarItemKind.StatlineWarning),
        new("tick", Bar, "Combat tick", "Countdown to the next combat round.", "Tick 2.4", Mono: true),
        new("hptick", Bar, "HP tick", "Countdown to the next HP regen tick (and the resting one while resting).", "HP 12.0", Mono: true),
        new("matick", Bar, "Mana tick", "Countdown to the next mana regen tick (and the meditating one while meditating).", "MA 8.5", Mono: true),
        new("connection", Bar, "Connection light", "Red idle, yellow connecting, green connected, with the reconnect countdown.", "Connected", StatusBarItemKind.ConnectionLight),

        new("profile", Who, "Profile name", "The loaded character profile.", "Cidir"),
        new("name", Who, "Character name", "Your character's name as the game reports it.", "Cidir Stormcrow"),
        new("level", Who, "Level", "Your trained level.", "Lvl 35"),
        new("class", Who, "Race and class", "Your race and class.", "Half-Elf Mystic"),
        new("lives", Who, "Lives", "Lives remaining.", "Lives 9"),
        new("bbs", Who, "BBS", "The BBS (and realm) this character plays on.", "ParadigmBBS:Paradigm"),
        new("gamedata", Who, "Game data set", "The active game data set.", "Paradigm"),
        new("combatprofile", Who, "Combat profile", "The active combat spell profile: its number and name.", "P1 Default"),

        new("hp", Vitals, "HP", "Current and maximum HP.", "HP 182/240"),
        new("hppct", Vitals, "HP percent", "HP as a percentage.", "HP 76%"),
        new("mana", Vitals, "Mana", "Current and maximum mana (or kai).", "MA 61/88"),
        new("manapct", Vitals, "Mana percent", "Mana as a percentage.", "MA 69%"),
        new("position", Vitals, "Posture", "Resting or meditating; empty while standing.", "Resting"),
        new("stealth", Vitals, "Stealth", "Sneaking or hidden; empty otherwise.", "Sneaking"),
        new("encumbrance", Vitals, "Encumbrance", "How loaded you are.", "Medium"),

        new("roomkey", Where, "Map / room number", "The room you are in, as map/room.", "1/297"),
        new("room", Where, "Room name", "The name of the room you are in.", "Town Square"),
        new("loop", Where, "Loop name", "The running loop; empty when none.", "Sewer circuit"),
        new("lap", Where, "Lap", "The lap of the running loop; empty when none.", "Lap 12"),
        new("destination", Where, "Walk destination", "Where a walk-to is headed; empty when none.", "To 1/1376"),

        new("fighting", Fight, "Combat target", "The monster you are attacking; empty out of combat.", "orc chieftain"),
        new("exprate", Fight, "Exp per hour", "This session's exp rate.", "1.2M/hr"),
        new("exptnl", Fight, "Exp to next level", "Exp still needed for the next level.", "TNL 2.4M"),
        new("sessionexp", Fight, "Session exp", "Exp earned this session.", "Exp 5.7M"),
        new("kills", Fight, "Session kills", "Monsters killed this session.", "Kills 412"),
        new("sessiontime", Fight, "Time online", "How long this session has been connected.", "3:14:07"),
        new("party", Fight, "Party", "Party size and leader; empty when solo.", "Party 4 · Tank leads"),

        new("auto", Other, "Auto engines", "Which automatic engines are on.", "Auto: Combat Nuke Heal Rest Bless"),
        new("cash", Other, "Cash carried", "The coins you are carrying.", "Cash 3p 40g"),
        new("clock", Other, "Clock", "Your computer's time.", "21:42"),
        new(CustomTextId, Other, "Custom text…", "Your own text. Put another item's name in braces to show its value, for example: Lap {lap} of {loop}.", "Your text", StatusBarItemKind.CustomText),
    };

    public static StatusBarItemDef? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    // A custom text with each {name} replaced by that item's value. A name that
    // isn't an item is left as typed, so a typo is visible rather than swallowed.
    public static string ExpandTokens(string? template, Func<string, string> valueOf)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        StringBuilder text = new(template.Length + 32);
        int at = 0;
        while (at < template.Length)
        {
            int open = template.IndexOf('{', at);
            int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (close < 0)
            {
                text.Append(template, at, template.Length - at);
                break;
            }
            text.Append(template, at, open - at);
            string name = template[(open + 1)..close];
            if (Find(name) is { Kind: not StatusBarItemKind.CustomText } def) text.Append(valueOf(def.Id));
            else text.Append(template, open, close - open + 1);
            at = close + 1;
        }
        return text.ToString();
    }
}
