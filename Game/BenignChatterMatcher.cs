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
    // suicide-password advisory block, a regen / illumination status label, or one of
    // the fixed-wording lines below that some parser reads without a router pattern
    // (bank, level-up, death and corpse, `profile` and `abil` rows, channel and gang
    // notices, the day cycle).
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
            || StatusLabelRx().IsMatch(text)
            || !HasLetterOrDigit(text)
            || BankRx().IsMatch(text)
            || OwnStateRx().IsMatch(text)
            || LevelUpRx().IsMatch(text)
            || DeathAndCorpseRx().IsMatch(text)
            || ProfileRowRx().IsMatch(text)
            || StatAllRowRx().IsMatch(text)
            || MonsterCastFailedRx().IsMatch(text)
            || ChannelRx().IsMatch(text)
            || GangRx().IsMatch(text)
            || DayCycleRx().IsMatch(text)
            || QuestFlagRowRx().IsMatch(text)
            || MiscRx().IsMatch(text)
            || ListingHeaderRx().IsMatch(text)
            || ListingRowRx().IsMatch(text)
            || UnreadPromptRx().IsMatch(text)
            || CounterstrikeRx().IsMatch(text)
            || GlancesOffRx().IsMatch(text)
            || OthersDodgeRx().IsMatch(text)
            || OthersDoorRx().IsMatch(text)
            || PartyDisbandedRx().IsMatch(text)
            || ExitListTailRx().IsMatch(text)
            || PlayerLookRowRx().IsMatch(text);
    }

    // The first line of a server listing whose rows are free text: the shop stock
    // table, a top list, the `set` help, the `profile` readout, Paradigm's `abil` readout,
    // a gang roster, a look at a player ("[ Name ](Gang)", then their description and
    // gear). The watcher skips from here to the next prompt.
    public static bool IsListingHeader(string text) =>
        !string.IsNullOrEmpty(text) && ListingHeaderRx().IsMatch(text);

    // An "Also here:" or "You notice" room row. A long one wraps, and the rows after
    // it are bare names the watcher skips until the sentence ends.
    public static bool IsRoomListRow(string text) =>
        text.StartsWith("Also here:", StringComparison.Ordinal)
        || text.StartsWith("You notice ", StringComparison.Ordinal);

    // Whether a room row stopped mid-list (no closing punctuation), so more follows.
    public static bool RoomListRowWraps(string text) =>
        text.Length > 0 && text[^1] is not ('.' or '!' or '?');

    // Splash art and table rules ("------", "-=-=-=-") carry no words at all.
    private static bool HasLetterOrDigit(string text)
    {
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    // Another player changing gear ("X wields / wears / removes <item>!"). Roster-gated: only a
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

    [GeneratedRegex(
        @"^(?:Item\s{2,}Quantity\s{2,}Price$|Top .+ of the Realm\b|The SET command is used to change|Player ID:\s+\d+$|HP Regen:\s+\S+\s+AC vs Evil:|.+ members \(\d+\)$"
      + @"|\[ [A-Z][\w '-]* \](?:\(.*\))?$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ListingHeaderRx();

    // Rows of those listings that are recognizable on their own, for one that lands
    // outside its block: an `abil` attack or spell row and their column heads, a
    // top-list row, a shop stock row, a gang roster row, a `set` help row.
    [GeneratedRegex(
        @"^(?:(?:Attack|Bash|Smash|Backstab|Punch|Kick|Jumpkick)\s+[\d.]+\s+\d+\s+\d+\s+\d+\b.*"
      + @"|Type\s+Swings\s+Accy\b.*|Short Name\s+Casts\s+Diff\b.*"
      + @"|\w{2,5}\s+\d+\s+\d+\s+\d+\s+\d+\s+\d+"
      + @"|Rank\s+Name\s+Class\b.*|\d+\. \S.*\s{2,}\d+"
      + @"|.+\s{2,}\d+\s{2,}\d+ [a-z]+ [a-z]+(?: \(.+\))?"
      + @"|.+\s{2,}\d+ \w+ \w+\s+- (?:Online|Offline)\b.*"
      + @"|Set \w+\s+- .+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ListingRowRx();

    // The bank's balance readout and the deposit / withdraw acknowledgements.
    [GeneratedRegex(
        @"^(?:Your balance at .+ is:|On deposit: \d+ .+ \[.+\]|You (?:deposit|withdrew) \d+ .+\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BankRx();

    // Our own posture and gear acknowledgements, and the `set` command's "Done.".
    [GeneratedRegex(
        @"^(?:You are now resting\.|You are no longer sneaking\.|You are now holding .+\.|Done\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OwnStateRx();

    [GeneratedRegex(
        @"^(?:Welcome to level \d+!|You gain \d+ additional lives\.|You gain \d+ CPs)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LevelUpRx();

    [GeneratedRegex(
        @"^(?:But, due to a miracle, you have been saved\.|You have \d+ lives left\.|You begin to pick through the corpse of .+\.\.\.|You have recovered the corpse of .+\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeathAndCorpseRx();

    // A row of the `profile` readout, for one that lands outside its block.
    [GeneratedRegex(
        @"^(?:Player ID|Life for this CHAR|Display Mode|Statusline|Broadcast Channel|Talking speed|Follow Mode|Receive Items|Warn on Evil|Block Entrance Msg):?\s{2,}\S",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProfileRowRx();

    // A label row of Paradigm's `abil` readout, likewise.
    [GeneratedRegex(
        @"^(?:(?:HP Regen|MA Regen|Max HP|Encum|vs Good|Crits|Spell Damage):\s+\S|(?:Attacks|Spells):$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StatAllRowRx();

    // Paradigm's wording for a monster's spell that failed or was resisted. It names
    // the spell, so it is never the spell's own message.
    // The same line names a party member when the spell was aimed at one of them.
    [GeneratedRegex(@"^.+ attempts to cast .+ on (?:you, but (?:fails\.|you resist!)|.+, but fails\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex MonsterCastFailedRx();

    [GeneratedRegex(@"^\w[\w '-]* just (?:joined|left) your channel \(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelRx();

    [GeneratedRegex(@"^(?:.+ has invited you to join .+\.|You have joined the gang .+\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex GangRx();

    [GeneratedRegex(
        @"^A new day (?:begins to approach\.|is fast approaching\.|is imminent\.|has come!)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DayCycleRx();

    // A quest-flag readout row: "TarlChain(210)             0".
    [GeneratedRegex(@"^\w+\(\d+\)\s+-?\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex QuestFlagRowRx();

    // One player sizing up another, and a `get` for coins that aren't there.
    [GeneratedRegex(@"^(?:\w+ looks \w+ up and down\.|You don't see any [\w ]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MiscRx();

    // A default-shaped statline the active pattern didn't read (it differs by a
    // space, say), with whatever was typed after it, and a typed `set statline`.
    [GeneratedRegex(@"^(?:\[HP=\d+/\d+[^\]]*\]\s*:|set statline \S)", RegexOptions.CultureInvariant)]
    private static partial Regex UnreadPromptRx();

    // Damage handed back by a counterstrike, ours or anyone's. Each monster name and
    // each amount made a line of its own in the queue.
    [GeneratedRegex(
        @"^(?:A counterstrike at .+ does \d+ damage!|You counterstrike .+ for \d+ damage!)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CounterstrikeRx();

    // A blow that landed and did nothing: a monster's on someone, or ours on a monster.
    [GeneratedRegex(
        @"^(?:The .+, but the swing glances off!|Your \w+ glances off .+!)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GlancesOffRx();

    // A swing someone else dodged ("…, but she dodges out of the way!", "…, but Boost
    // dodges!"). Our own dodges are routed patterns.
    [GeneratedRegex(
        @"^.+, but (?:he|she|it|they|[A-Z][\w'-]*) dodges?(?: out of the way)?!$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OthersDodgeRx();

    // What the room sees of someone working a door or gate.
    [GeneratedRegex(
        @"^You see .+? (?:bash|unlock|lock|open|close|pick the lock on) the (?:door|gate) "
      + @"(?:to the (?:north|south|east|west|northeast|northwest|southeast|southwest|up|down)|above you|below you)\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OthersDoorRx();

    // PartyManager reads this off the wire itself.
    [GeneratedRegex(@"^Your party has been disbanded\.$", RegexOptions.CultureInvariant)]
    private static partial Regex PartyDisbandedRx();

    // The tail of an "Obvious exits:" row that wrapped: nothing but exits ("west",
    // "door west, down").
    [GeneratedRegex(
        @"^(?:(?:(?:open|closed|locked) )?(?:door|gate) )?(?:north|south|east|west|northeast|northwest|southeast|southwest|up|down)"
      + @"(?:, (?:(?:(?:open|closed|locked) )?(?:door|gate) )?(?:north|south|east|west|northeast|northwest|southeast|southwest|up|down))*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExitListTailRx();

    // Rows of a look at a player, for one that lands outside its block: the gear
    // heading, a worn item with its slot, and the wound line (whole, or the tail of
    // one that wrapped).
    [GeneratedRegex(
        @"^(?:(?:He|She|It) is equipped with:"
      + @"|\S.*\S\s{2,}\((?:Head|Ears|Eyes|Face|Neck|Back|Torso|Arms|Wrist|Hands|Finger|Waist|Legs|Feet|Worn|Weapon Hand|Off-Hand|Two-Handed)\)"
      + @"|(?:(?:He|She|It) (?:is|appears to be) )?(?:unwounded|(?:slightly|moderately|heavily|severely|critically|very critically|mortally) wounded)\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLookRowRx();

    // The `br` broadcast-channel status: a header followed by the member list. The list
    // is bare player names, indistinguishable from other text on its own, so the watcher
    // suppresses it ONLY on the lines right after the header (stateful) — these two
    // stateless helpers feed that gate.

    // "The following users are on channel N:" — the list header.
    public static bool IsChannelListHeader(string text) => ChannelListHeaderRx().IsMatch(text);

    // A member-list row: one or more capitalized player names, comma- or space-separated,
    // optional trailing period. Only trusted immediately after the header (see the watcher).
    public static bool LooksLikeChannelMemberList(string text) => ChannelMemberListRx().IsMatch(text);

    [GeneratedRegex(@"^The following users are on channel \d+:$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelListHeaderRx();

    [GeneratedRegex(@"^[A-Z][\w'-]*(?:,? +[A-Z][\w'-]*)*\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelMemberListRx();

    [GeneratedRegex(@"^(?<name>\w[\w '-]*) (?:wields|wears|removes) .+!$", RegexOptions.CultureInvariant)]
    private static partial Regex GearSwapRx();
}
