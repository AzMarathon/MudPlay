namespace MudPlay.Game.Combat;

// Whether an attack command is a martial-arts strike (punch / kick / jumpkick). A
// strike is thrown with the hands or feet whatever is wielded, so the weapon's
// magic says nothing about what it can hurt (GAME_MECHANICS "Weapons: "no effect"
// lines and the magical-weapon requirement").
public static class MartialArtsCommand
{
    // The strikes' command words. Players usually type them short (`pu`, `ki`,
    // `ju`), so any lead of two letters or more counts.
    private static readonly string[] Strikes = { "punch", "kick", "jumpkick" };
    private const int ShortestForm = 2;

    public static bool IsStrike(string? attackCommand)
    {
        if (string.IsNullOrWhiteSpace(attackCommand)) return false;
        string word = attackCommand.Trim().Split(' ', 2)[0];
        if (word.Length < ShortestForm) return false;
        foreach (string strike in Strikes)
            if (strike.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
