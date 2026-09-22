using System;
using System.Text.RegularExpressions;

namespace MudPlay.Game;

// Recognizers for benign, non-spell wire chatter the unrecognized-line watcher should
// exclude from its review queue: player movement / social / status lines that no spell
// record will ever describe. Each shape is anchored tightly so it can never swallow a
// genuine unknown spell / proc line — surfacing those is the whole point of the queue.
// These gate the REPORT only; they change no client behavior (routing, chat, party,
// movement are all untouched). Emotes / socials are handled separately, straight off the
// wire's colour via ActionEmoteClassifier, because they can't be told from other text
// without it.
public static partial class BenignChatterMatcher
{
    // A line that's known benign non-spell chatter: a player departure, disconnect,
    // follow notice, toll payment, an empty self-say, an "Also here:" roster row, the
    // suicide-password advisory block, or a regen / illumination status label.
    public static bool IsBenign(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return DepartureRx().IsMatch(text)
            || DisconnectRx().IsMatch(text)
            || FollowRx().IsMatch(text)
            || TollRx().IsMatch(text)
            || EmptySayRx().IsMatch(text)
            || AlsoHereRx().IsMatch(text)
            || SuicideAdvisoryRx().IsMatch(text)
            || StatusLabelRx().IsMatch(text);
    }

    // Another player changing gear ("X wears / removes <item>!"). Roster-gated: only a
    // KNOWN player's name qualifies, so a same-shaped monster / spell line ("The lich
    // removes its own head!") can never be suppressed by mistake.
    public static bool IsOtherPlayerGearSwap(string text, Func<string, bool> isKnownPlayer)
    {
        Match m = GearSwapRx().Match(text);
        return m.Success && isKnownPlayer(m.Groups["name"].Value);
    }

    // "X just left to the <compass direction>." — the player-departure notice. The
    // monster-flee shapes ("walks out of the room to …", "<verb> out to …") are a
    // separate routed pattern; up / down aren't included since their exact wording isn't
    // confirmed (they simply keep surfacing rather than risk a wrong guess).
    [GeneratedRegex(
        @"^[A-Z].* just left to the (?:north|south|east|west|northeast|northwest|southeast|southwest)\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DepartureRx();

    // "X just disconnected!!!" — recognition only, so it's excluded from the report
    // whether or not a ChatRouter custom disconnect pattern is configured. Optional
    // trailing period covers both the standard and the no-period board forms.
    [GeneratedRegex(@"^\w[\w '-]* just disconnected!!!\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectRx();

    // The follow target is a player NAME (capitalized), not lowercase prose — so a
    // tracking-style "You are following the trail…" line can't be suppressed.
    [GeneratedRegex(@"^You are following [A-Z][\w '-]*\.$", RegexOptions.CultureInvariant)]
    private static partial Regex FollowRx();

    [GeneratedRegex(@"^You just paid \d+ .+ in toll charges\.$", RegexOptions.CultureInvariant)]
    private static partial Regex TollRx();

    // The local character's own empty say (typed a bare `say`). Recognized here rather
    // than by widening the chat pattern, which would log a blank conversation entry.
    [GeneratedRegex(@"^You say """"$", RegexOptions.CultureInvariant)]
    private static partial Regex EmptySayRx();

    // Any "Also here:" roster row. The period-terminated form is already routed; this
    // also covers the wrapped first line (no trailing period) a narrow terminal splits.
    [GeneratedRegex(@"^Also here: ", RegexOptions.CultureInvariant)]
    private static partial Regex AlsoHereRx();

    // The login suicide-password advisory block — four fixed wrapped lines, matched by
    // their distinctive leads.
    [GeneratedRegex(
        @"^(?:To prevent accidental suicide or reroll|have been password protected|your suicide password, so please|SET SUICIDE command\.)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SuicideAdvisoryRx();

    [GeneratedRegex(@"^(?:Regen Time|Room Illu):\s", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLabelRx();

    [GeneratedRegex(@"^(?<name>\w[\w '-]*) (?:wears|removes) .+!$", RegexOptions.CultureInvariant)]
    private static partial Regex GearSwapRx();
}
