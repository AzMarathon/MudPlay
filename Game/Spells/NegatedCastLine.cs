using System.Text.RegularExpressions;

namespace MudPlay.Game.Spells;

// The game's lines for a cast that reached its target and did nothing to it: the
// target's magic resistance threw it off, or something the target wears negates
// that spell (GAME_MECHANICS "Magic Resist (M.R.) and `TypeOfResists`"). One cast
// prints up to three of them, one to each party:
//
//   to the caster   You attempt to cast holding breath at Aldric, but it's negated!
//   to the target   You negate Aldric's cast of holding breath!
//   to the room     Aldric negates Aldric's cast of holding breath!
//
// That is Paradigm's wording. Stock's says "resisted" in the same three places
// ("You attempt to cast %s at %s, but the spell is resisted.", "You resisted %s's
// cast of %s.", "%s resisted %s's cast of %s.", and without "cast of" when the
// caster is a player).
//
// Each names the spell, so none is a spell's own message, and none means the spell
// took hold: read here so nothing takes it for an unknown spell line or an effect.
// Whole line only, and a name is letters, spaces, apostrophes and hyphens, so a
// chat line quoting one of these is left alone.
public static partial class NegatedCastLine
{
    // Self is the game's "You" on the side the character is.
    public const string Self = "You";

    public readonly record struct Cast(string Caster, string Target, string Spell);

    public static bool TryRead(string? line, out Cast cast)
    {
        cast = default;
        if (string.IsNullOrEmpty(line)) return false;
        // Every game line comes through here: the two words first, the patterns
        // only for a line that has one.
        if (!line.Contains("negate", StringComparison.Ordinal) && !line.Contains("resisted", StringComparison.Ordinal))
            return false;
        string text = line.Trim();

        if (ToCasterRx().Match(text) is { Success: true } own)
            cast = new Cast(Self, own.Groups["target"].Value, own.Groups["spell"].Value);
        else if (ToTargetRx().Match(text) is { Success: true } mine)
            cast = new Cast(mine.Groups["caster"].Value, Self, mine.Groups["spell"].Value);
        else if (ToRoomRx().Match(text) is { Success: true } seen)
            cast = new Cast(seen.Groups["caster"].Value, seen.Groups["target"].Value, seen.Groups["spell"].Value);
        else
            return false;
        return true;
    }

    [GeneratedRegex(
        @"^You attempt to cast (?<spell>.+) at (?<target>[\w' -]+), but (?:it's negated!|the spell is resisted\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ToCasterRx();

    [GeneratedRegex(
        @"^You (?:negate (?<caster>[\w' -]+?)'s cast of (?<spell>[^!]+)!|resisted (?<caster>[\w' -]+?)'s (?:cast of )?(?<spell>[^.]+)\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ToTargetRx();

    [GeneratedRegex(
        @"^(?<target>[\w' -]+?) (?:negates (?<caster>[\w' -]+?)'s cast of (?<spell>[^!]+)!|resisted (?<caster>[\w' -]+?)'s (?:cast of )?(?<spell>[^.]+)\.)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ToRoomRx();
}
