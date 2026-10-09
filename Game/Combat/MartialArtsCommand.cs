namespace MudPlay.Game.Combat;

// Whether an attack command is a martial-arts strike (punch / kick / jumpkick). A
// strike is thrown with the hands or feet whatever is wielded, so the weapon's
// magic says nothing about what it can hurt (GAME_MECHANICS "Weapons: "no effect"
// lines and the magical-weapon requirement").
public static class MartialArtsCommand
{
    // The strikes' command words and the shortest lead the game takes for each, on
    // both realms: `pu`, `kic`, `ju`. `ki` is no command (GAME_MECHANICS
    // "Martial-arts strikes are class-innate").
    private static readonly (string Word, int ShortestForm)[] Strikes =
    {
        ("punch", 2), ("kick", 3), ("jumpkick", 2),
    };

    public static bool IsStrike(string? attackCommand)
    {
        if (string.IsNullOrWhiteSpace(attackCommand)) return false;
        string word = attackCommand.Trim().Split(' ', 2)[0];
        foreach ((string strike, int shortest) in Strikes)
            if (word.Length >= shortest && strike.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
