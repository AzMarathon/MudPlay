using System.Text;

namespace MudPlay.Tests;

// The game's side of a `train stats`, in the shapes Paradigm put on the wire in
// reports paradigm-20261026-225703, -225718 and -225733: the prompt redrawn in
// place, the command echoed on it, party lines that wipe the prompt row, the room
// a bare Enter redraws, and the Character Editor painted whole after a clear
// screen, a row per line, the cursor parked on the Family Name box. Fed through a
// ServerFeed, each string is what one server read carried.
internal static class TrainStatsWire
{
    public const string Statline = "[HP=318/KAI=14]:";

    // Every row the game rewrites starts by sending the cursor left and erasing.
    private const string Rewrite = "\u001b[79D\u001b[K";

    // The prompt, drawn in place and left waiting.
    public static string Prompt() => Rewrite + Statline;

    // The game's echo of a command on its prompt row, ahead of the command's answer.
    public static string Echo(string command) => Rewrite + Statline + command + "\r\n";

    // A line of another player's, printed over the redrawn prompt.
    public static string PartyLine(string text) => Rewrite + Statline + "\r\u001b[2K" + text + "\r\n";

    // A bare Enter's answer: its empty echo, then the room drawn again.
    public static string RoomRedrawn() =>
        Echo(string.Empty)
        + Rewrite + "Ancient Keep, Throne Room\r\n"
        + "You notice 1 gold crown, 10 silver nobles here.\r\n"
        + "Also here: Aunty, Barter, Thunder, Ironbar*, aged titan.\r\n"
        + "Obvious exits: north\r\n";

    // The Character Editor, as the game paints it after each keystroke.
    public static string Form(int strength, int cpLeft, string familyName = "")
    {
        StringBuilder form = new("\u001b[2J\u001b[H");
        Row(form, "    .─────────────────────────────────────.──.");
        Row(form, "   / M A J O R  M U D Character Editor /    \\  ┌─    Point Cost Chart    ─┐");
        Row(form, "  │                                     ├──.   │ │                          │");
        Row(form, "  │ » Given Name   Someone            « │___\\_/  │ 1st 10 points: 1 CP each │");
        Row(form, $"  │ » Family Name  {familyName,-18} « │        │ 2nd 10 points: 2 CP each │");
        Row(form, "  │ » Race         Half-Orc           « │        │ 3rd 10 points: 3 CP each │");
        Row(form, "  │ » Class        Mystic             « │        │     ... and so on ...    │");
        Row(form, "  │                                     │        │                          │");
        Row(form, $"  │ » Strength   (  55 to  160)  {strength,4} « │        │ +10 to base stat:  10 CP │");
        Row(form, "  │ » Intellect  (  30 to  130)    40 « │ ───────┤ +20 to base stat:  30 CP │");
        Row(form, "  │ » Willpower  (  30 to  135)    30 « │        │ +30 to base stat:  60 CP │");
        Row(form, "  │ » Agility    (  45 to  145)   100 « │        │ +40 to base stat: 100 CP │");
        Row(form, "  │ » Health     (  55 to  165)    60 « │        │ +50 to base stat: 150 CP │");
        Row(form, "  │ » Charm      (  30 to  120)    30 « │        └─    ... and so on ...   ─┘");
        Row(form, "  │                                     │");
        Row(form, "  │ »  Hair Length   None             « │        ┌ Use the Space Bar to");
        Row(form, "  │ »  Hair Colour   Black            « │ ───────┤ toggle between choices for");
        Row(form, "  │ »  Eye Colour    Black            « │        └ your physical description");
        Row(form, "  │                                     │");
        Row(form, $"  │ »  Exit: SAVE «  » CP Left:    {cpLeft,4} « │ ──────── SAVE, EDIT or QUIT");
        Row(form, " ⌐┴─────────────────────────────────.     │");
        Row(form, " \\_________________________________\\___/");
        return form.Append("\u001b[5;36H").ToString();
    }

    // Leaving the form with SAVE: the screen cleared, the save line, the realm
    // re-entry, and the prompt left waiting in place.
    public static string Saved(string name = "Someone") =>
        "\u001b[2J\u001b[HTraining complete! Stats updated.\r\n"
        + name + " just entered the Realm.\r\n"
        + Prompt();

    private static void Row(StringBuilder form, string row) => form.Append(row).Append("\r\n");
}
