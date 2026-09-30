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
public static partial class ShieldReflectLines
{
    [GeneratedRegex(
        @"^(?:.+ is (?:scorched|shocked|spiked) for \d+ damage!"
        + @"|You are (?:scorched|shocked|stabbed by spikes) for \d+ damage!"
        + @"|The (?:collar spikes|armour spikes|shield spikes?) stabs? .+ for \d+ damage!"
        + @"|The blades slash .+ for \d+ damage!"
        + @"|A counterstrike at .+ does \d+ damage!)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reflect();

    public static bool IsReflect(string line) => Reflect().IsMatch(line);
}
