using System.Text;
using MudPlay.Terminal;

namespace MudPlay.Tests;

// The game's side of a `train stats`, in the shapes the Anomaly server put on the wire in
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

    // The `Char. Creation` box (captures paradigm-20260805-*), painted with cursor
    // positioning at the layout of report paradigm-20260930-160602: labels at
    // column 7 of rows 9-14, ranges at column 18, values right-aligned from column
    // 34, CP Left on row 20. Its title row, with the panel, is painted last and the
    // cursor left on it, so the row never completes as a line.
    public static string CreationBox(int strength, int cpLeft)
    {
        string[] labels = { "Strength", "Intellect", "Willpower", "Agility", "Health", "Charm" };
        string[] ranges = { "(  55 to  160)", "(  30 to  130)", "(  30 to  135)", "(  45 to  145)", "(  55 to  165)", "(  30 to  120)" };
        int[] values = { strength, 40, 30, 100, 60, 30 };
        StringBuilder box = new("\u001b[2J\u001b[1;1H");
        box.Append("\u001b[5;3H| > Family Name\u001b[5;39H<");
        for (int i = 0; i < labels.Length; i++)
        {
            int row = 9 + i;
            box.Append($"\u001b[{row};3H| > {labels[i]}\u001b[{row};39H<\u001b[{row};41H|");
            box.Append($"\u001b[{row};18H{ranges[i]}");
            box.Append($"\u001b[{row};34H{values[i],4}");
        }
        box.Append($"\u001b[20;3H| >  Exit:\u001b[20;14HSAVE\u001b[20;19H<\u001b[20;22H> CP Left:\u001b[20;33H{cpLeft,4}\u001b[20;39H<");
        return box.Append("\u001b[2;4H/ P A R A D I G M      Char. Creation /    \\  --    Point Cost Chart    --").ToString();
    }

    // Stock's scrolling form: its lines are Stock-sourced, as `wccmmud.dll` 1.11p
    // prints them (GAME_MECHANICS "The `train stats` screen (Char. Creation
    // box)"); the frame around the Point Cost Chart, and only one of its rows, are
    // drawn here in ASCII.
    public static string ScrollingForm(int cp) =>
        "M A J O R   M U D\r\n"
        + "Character Generator\r\n"
        + "+---- Point Cost Chart ----+\r\n"
        + "| 1st 10 points: 1 CP each |\r\n"
        + "+--------------------------+\r\n"
        + "Strength  55 - 160\r\n"
        + "Intellect 30 - 130\r\n"
        + "Willpower 30 - 135\r\n"
        + "Agility   45 - 145\r\n"
        + "Health    55 - 165\r\n"
        + "Charm     30 - 120\r\n"
        + $"You have {cp} CP to distribute.\r\n";

    // Stock's refusal of `train stats` where there's no trainer, as `wccmmud.dll`
    // 1.11p prints it; Paradigm's wording isn't recorded.
    public const string StockNotATrainer = "You may not train your stats here!";

    // Leaving the form with SAVE: the screen cleared, the save line, the realm
    // re-entry, and the prompt left waiting in place.
    public static string Saved(string name = "Someone") =>
        "\u001b[2J\u001b[HTraining complete! Stats updated.\r\n"
        + name + " just entered the Realm.\r\n"
        + Prompt();

    private static void Row(StringBuilder form, string row) => form.Append(row).Append("\r\n");

    // The visible screen as newline-joined rows, the way the terminal session
    // flattens it for the trainer screen's readers.
    public static string ScreenText(TerminalScreen screen)
    {
        StringBuilder text = new();
        for (int y = 0; y < screen.Rows; y++)
        {
            foreach (Cell cell in screen.Row(y)) text.Append(cell.Char);
            text.Append('\n');
        }
        return text.ToString();
    }
}
