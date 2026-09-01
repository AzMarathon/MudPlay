using System;
using System.Collections.Generic;
using System.Globalization;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// Resolves the argument of @profile (and any name-based lookup) to a profile
// index. A bare number is the 1-based position; otherwise the argument
// best-matches a profile NAME and the single closest candidate is returned —
// exact (case-insensitive) beats a prefix match beats a substring match beats a
// token-subset match, with the shorter (closer) name winning ties. Never reports
// "ambiguous": the user asked for the best match, so we give them one. Returns
// null only when the argument is empty, or no configured name contains it at all.
public static class CombatSpellProfileMatcher
{
    public static int? Resolve(IReadOnlyList<CombatSpellProfile>? profiles, string? arg)
    {
        if (profiles is null || profiles.Count == 0) return null;
        string q = (arg ?? string.Empty).Trim();
        if (q.Length == 0) return null;

        // A bare integer in range is the 1-based slot number.
        if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n >= 1 && n <= profiles.Count)
            return n - 1;

        int best = -1, bestScore = 0, bestLen = int.MaxValue;
        for (int i = 0; i < profiles.Count; i++)
        {
            string name = profiles[i].Name?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            int score = Score(name, q);
            if (score <= 0) continue;
            if (score > bestScore || (score == bestScore && name.Length < bestLen))
            {
                best = i;
                bestScore = score;
                bestLen = name.Length;
            }
        }
        return best >= 0 ? best : null;
    }

    // Higher = closer. Exact » prefix » substring » token-subset. Longer names
    // score slightly lower within a tier so "fire" prefers "Fire" over "Firestorm".
    private static int Score(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 500 - Math.Min(400, name.Length - query.Length);
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 200 - Math.Min(150, name.Length - query.Length);
        return AllTokensContained(name, query) ? 50 : 0;
    }

    // Every whitespace-delimited token of the query appears (case-insensitively) in
    // the name — matches the app's NameMatchesTokens semantics for multi-word args.
    private static bool AllTokensContained(string name, string query)
    {
        foreach (string tok in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!name.Contains(tok, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
