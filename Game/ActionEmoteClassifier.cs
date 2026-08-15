using System;
using System.Text.RegularExpressions;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Detects a BBS action / emote (a MUD social from the customizable `action list`)
// off a full-GREEN terminal line, and which point of view it is.
//
// Colour is the necessary gate: using an action is guaranteed an all-green line
// (ANSI index 2 — SGR 0;32, the same shade the board paints the WHOLE "Obvious
// exits:" line). All-green alone isn't sufficient, because that exits line is
// all-green too — so a green line is an action only when its HEAD matches an emote
// shape:
//   - Own POV: begins "You <lowercase verb> …" (an emote), not "You are/have/… "
//     (a status line).
//   - Others' POV: begins "<Player> <lowercase verb> …" where <Player> is a real
//     player currently in the room (verified by the caller) — which rejects room
//     names ("Obvious exits: …"), monsters, and ambient flavour ("A voice shouts
//     …", "Children rush past you …"), plus a denylist for enter/exit/follow/
//     logon/chat lines.
//
// The label-only greens ("Wealth:" / "Encumbrance:" / stat-row labels) are NOT
// all-green — only the label cell is index 2, the value is another colour — so they
// fail the colour gate before the head test ever runs; "You are carrying …" isn't
// green at all. The one fully-green non-action line is "Obvious exits:", caught by
// the head test (no You/player head, no terminal '.'/'!').
//
// The `action list` verb wording does NOT track the output ("jump" → "You leap …",
// "tickle" → "You look around looking for someone to tickle.") so there's no verb
// registry to match against — the head shape + colour is the reliable signal.
public static class ActionEmoteClassifier
{
    public enum Kind { None, Own, Other }

    // True when every non-blank char of the line is green (palette index 2 = SGR
    // 32 / "0;32", or 10 = bright green). Mirrors RoomDisplayParser's whole-line
    // bright-cyan test with the green indices.
    public static bool IsAllGreen(LineExtractor.EmittedLine line)
    {
        if (line.Attributes is null || line.Attributes.Length == 0) return false;
        int total = 0, green = 0;
        int len = Math.Min(line.Text.Length, line.Attributes.Length);
        for (int i = 0; i < len; i++)
        {
            if (char.IsWhiteSpace(line.Text[i])) continue;
            total++;
            if (IsGreen(line.Attributes[i])) green++;
        }
        return total > 0 && green == total;
    }

    private static bool IsGreen(CellAttributes attr)
    {
        if (attr.Foreground.Kind != ColorKind.Indexed) return false;
        int idx = (int)attr.Foreground.Value;
        return idx == 2 || idx == 10;   // SGR 32 (0;32 / 1;32) or SGR 92 (bright green)
    }

    // Defensive denylist: "You …" status prefixes that must never read as an emote
    // if one ever arrives all-green. (Status lines like "You are carrying …" aren't
    // green today, so this is belt-and-suspenders — but no emote output begins with
    // any of these, so it can't drop a real action.)
    private static readonly string[] OwnExclusions =
    {
        "You are ", "You have ", "You notice ", "You see ", "You hear ", "You feel ",
        "You sense ", "You say ", "You yell ", "You gossip", "You tell ", "You ask ",
        "You invoke ", "You gain ", "You now ", "You don't ", "You do not ",
        "You can't ", "You cannot ", "You fail", "You begin ", "You start ", "You stop ",
    };

    // Lines that begin with a name but are room enter/exit, party follow, logon or
    // chat — never an action, even when green.
    private static readonly Regex OtherExclusion = new(
        @"\b(walks? into the room|moves? into the room|just (left|entered|arrived)|" +
        @"has invited|invites you|start(ed)? to follow|stop(ped)? following|" +
        @"is (now )?following|just moved to|logs? o(n|ff)|says?|yells?|gossips?|" +
        @"telepaths?|gangpaths?|auctions?|broadcasts?|is looking at you|glances? at)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Others'-POV head: one capitalised name token, a space, then a lowercase
    // verb. Player names on the board are a single word. The "space + lowercase"
    // requirement rejects every "Label: value" green line (colon, not a space).
    private static readonly Regex OtherHead = new(
        @"^(?<name>[A-Z][A-Za-z'\-]+) [a-z]",
        RegexOptions.CultureInvariant);

    // Classify an already-green line. isKnownPlayer confirms an others'-POV actor
    // is a real player in the room / party (ambient flavour and monsters fail it).
    // Returns the POV and, for Other, the actor's name.
    public static Kind Classify(string text, Func<string, bool> isKnownPlayer, out string? actor)
    {
        ArgumentNullException.ThrowIfNull(isKnownPlayer);
        actor = null;
        string t = text.Trim();
        if (t.Length < 4) return Kind.None;
        if (t[^1] != '.' && t[^1] != '!') return Kind.None;   // an emote is a sentence

        if (t.StartsWith("You ", StringComparison.Ordinal))
        {
            foreach (string ex in OwnExclusions)
                if (t.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) return Kind.None;
            // "You <lowercase verb> …" is an emote; "You HAVE/ARE …" is a status line.
            return char.IsLower(t[4]) ? Kind.Own : Kind.None;
        }

        Match m = OtherHead.Match(t);
        if (!m.Success) return Kind.None;
        if (OtherExclusion.IsMatch(t)) return Kind.None;
        string name = m.Groups["name"].Value;
        if (!isKnownPlayer(name)) return Kind.None;
        actor = name;
        return Kind.Other;
    }
}
