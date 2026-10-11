namespace MudPlay.Game.Combat;

// The words the game takes for an attack on a monster, and for `break`, by the
// first word of a command line.
//
// The game matches a command word letter by letter against its own table, and each
// command answers only from a certain length on (GAME_MECHANICS "Command words and
// abbreviations", read from the Stock 1.11p command parser for every prefix).
// Paradigm's table isn't on record, so Stock's is used on both realms: a short form
// Paradigm doesn't take is a line the game does nothing with.
//
// OutboundAttackObserver keeps its own shorter list on purpose: it decides whose
// attack a round is, and has always counted only the forms a player types in a
// fight. This is the whole table, for a caller that must not miss an attack however
// it was spelt.
public static class AttackCommandWords
{
    // Whether word starts an attack. The martial-arts strikes (punch, kick, jumpkick)
    // are MartialArtsCommand's. `kil` / `kill` are no attack: the game parses them to
    // a command that does nothing.
    public static bool IsAttack(string? word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        return IsLead(word, "attack", 1)                                // a … attack
            || word.Equals("au", StringComparison.OrdinalIgnoreCase)    // attack too
            || IsBash(word)
            || word.Equals("bs", StringComparison.OrdinalIgnoreCase)
            || IsLead(word, "backstab", 5)                              // backs … backstab
            || IsLead(word, "smash", 3)                                 // sma, smas, smash
            || MartialArtsCommand.IsStrike(word)
            // The table also takes these two for punch; `purge` in full is another command.
            || word.Equals("pur", StringComparison.OrdinalIgnoreCase)
            || word.Equals("purg", StringComparison.OrdinalIgnoreCase);
    }

    // Whether word is the bash command under any of its names: `bas`, `bash`, `aa`,
    // `al` … `allout`, `forc`, `force`. With a direction after it the game bashes
    // the door on that exit and attacks nothing.
    public static bool IsBash(string? word)
        => word is { Length: > 0 }
           && (word.Equals("aa", StringComparison.OrdinalIgnoreCase)
               || IsLead(word, "bash", 3) || IsLead(word, "allout", 2) || IsLead(word, "force", 4));

    // Whether word is `break`: `bre`, `brea` or `break`. `br` is the channel
    // broadcast, and `breaks` is nothing.
    public static bool IsBreak(string? word) => word is not null && IsLead(word, "break", 3);

    // word is a spelling of full the game takes: its first letters, shortest or more.
    private static bool IsLead(string word, string full, int shortest)
        => word.Length >= shortest && full.StartsWith(word, StringComparison.OrdinalIgnoreCase);
}
