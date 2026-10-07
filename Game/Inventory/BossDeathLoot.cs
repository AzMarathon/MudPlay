using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Inventory;

// Which monster record's drops a boss's death left on the floor. Many bosses are
// more than one monster: the record on the boss list dies and its death spell
// summons the next (Lord Chisholm becomes the malformation, the mayor of Arlysia
// becomes arachnigoth, a neutral Lallim Whitemane becomes the hostile one), and the
// loot is on the one that comes last. Grab All read the listed record's drop table
// for every death, so it sprayed gets for the wrong monster's items, or for none.
//
// Given the boss's chain (its own record, then everything its death summons, in
// order) and what was seen of the death, this picks the records that could be the
// one that died:
//   - by name, when the death was named;
//   - then, among records sharing that name, by the exp the kill paid, when that
//     tells them apart (a neutral record worth nothing against the hostile one).
// What can't be told apart is all returned: a get for an item that isn't there costs
// a line, a missed one costs the loot.
public static class BossDeathLoot
{
    public readonly record struct ChainMonster(int Number, string Name, long Exp);

    // A kill's exp is shared by the party, six at most (GAME_MECHANICS "Party size
    // bounds"), so a monster worth E pays each of them somewhere from a sixth of E up
    // to all of it.
    private const int MaxPartySize = 6;

    public static bool CouldPay(long worth, long gained) =>
        worth > 0 && gained <= worth && gained >= worth / MaxPartySize - 1;

    // deadName null is a death nobody named (the boss vanished from its room, or
    // died in the dark): the listed record, the head of the chain.
    public static IReadOnlyList<int> RecordsThatDied(
        IReadOnlyList<ChainMonster> chain, string? deadName, int? expGained)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0) return Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(deadName)) return new[] { chain[0].Number };

        string dead = Normalize(deadName);
        List<ChainMonster> named = chain.Where(m => Normalize(m.Name) == dead).ToList();
        // A wrapped or decorated name ("the angry Lord Chisholm") still holds the
        // record's; the longest such name is the closest.
        if (named.Count == 0)
        {
            int best = chain.Where(m => Holds(dead, m.Name)).Select(m => m.Name.Length).DefaultIfEmpty(0).Max();
            named = chain.Where(m => Holds(dead, m.Name) && m.Name.Length == best).ToList();
        }
        if (named.Count == 0) return new[] { chain[0].Number };
        if (named.Count == 1) return new[] { named[0].Number };

        List<ChainMonster> byExp = expGained is { } gained and > 0
            ? named.Where(m => CouldPay(m.Exp, gained)).ToList()
            : named.Where(m => m.Exp <= 0).ToList();
        return (byExp.Count > 0 ? byExp : named).Select(m => m.Number).ToList();
    }

    private static bool Holds(string dead, string recordName)
    {
        string name = Normalize(recordName);
        return name.Length > 0 && dead.Contains(name, StringComparison.Ordinal);
    }

    // Lower-case, single-spaced, leading article dropped: a name off the wire is
    // word-wrapped and may carry "the".
    private static string Normalize(string s)
    {
        s = string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (string article in new[] { "the ", "an ", "a " })
            if (s.StartsWith(article, StringComparison.Ordinal)) return s[article.Length..];
        return s;
    }
}
