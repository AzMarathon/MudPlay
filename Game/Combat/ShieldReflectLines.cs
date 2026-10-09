using System.Text.RegularExpressions;

namespace MudPlay.Game.Combat;

// A damage shield's hit back at whoever physically struck its wearer — the
// "Shockshield Message" a spell or item's ability 137 names (shockshield, hellfire
// shield, chaos shield, bladed sphere, barbskin; spiked collars, armour and shields;
// a counterstrike). Stock 1.11p message table 1145 / 1446 / 2078 / 2877 / 2879 /
// 3050 / 3051 / 3136 / 3140, the same on Paradigm (user, 2026-09-29). The wearer's
// own view ("You spike …", "Your blades slash …", "You counterstrike …") names us
// as the dealer already; these are the views that don't say whose shield it was:
//   struck someone else, as the wearer or anyone else sees it — "<attacker> is
//   scorched for N damage!", "The shield spikes stab <attacker> …";
//   struck us — "You are scorched for N damage!", "The blades slash you …".
// Monsters carry the same ability with messages of their own: the vision's
// "<attacker> is burned …" (Stock message 1703) and the razor boar's "<attacker> is
// jabbed by spines …" (Paradigm message 3837, report paradigm-20261009-023430).
public static partial class ShieldReflectLines
{
    [GeneratedRegex(
        @"^(?:.+ is (?:scorched|shocked|spiked|burned|jabbed by spines) for \d+ damage!"
        + @"|You are (?:scorched|shocked|burned|stabbed by spikes|jabbed by spines) for \d+ damage!"
        + @"|The (?:collar spikes|armour spikes|shield spikes?) stabs? .+ for \d+ damage!"
        + @"|The blades slash .+ for \d+ damage!"
        + @"|A counterstrike at .+ does \d+ damage!)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reflect();

    public static bool IsReflect(string line) => Reflect().IsMatch(line);

    // The shape every struck-back line of a shield message has, whatever its verb:
    // "You are <struck> for N damage!" / "<name> is <struck> for N damage!". Paradigm's
    // monsters carry shield messages whose text isn't in the game data, so a line of
    // this shape counts as a shield's only next to proof of the shield itself: it
    // directly follows the victim's own hit on a monster the data gives one.
    [GeneratedRegex(@"^(?:You are|.+ is) .+ for \d+ damage!\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StruckBack();

    public static bool HasStruckBackShape(string line) => StruckBack().IsMatch(line);
}
