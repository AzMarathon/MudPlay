using System.Text.RegularExpressions;

namespace MudPlay.Game;

// One line of the `stat` screen's active-effect list, as printed minus any
// remaining-time tail. Timed says the tail was there: Paradigm prints one on
// every effect, Stock never does, so on a Paradigm screen an untimed line is
// something else that landed inside the screen (GAME_MECHANICS "The `stat`
// screen's buff readout is never a fresh cast").
public readonly partial record struct StatusEffectLine(string Text, bool Timed)
{
    // The line ends in a remaining-time tail — "(411s)", "(6m 51s)", "(1h)". A
    // fresh-cast effect line has none.
    public static bool HasCountdown(string line) => Countdown().IsMatch(line);

    public static StatusEffectLine Parse(string line)
    {
        Match tail = Countdown().Match(line);
        return tail.Success
            ? new StatusEffectLine(line[..tail.Index].TrimEnd(), true)
            : new StatusEffectLine(line.Trim(), false);
    }

    [GeneratedRegex(@"\(\d+[dhms]( \d+[dhms])*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Countdown();
}
