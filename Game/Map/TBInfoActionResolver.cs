using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Resolves a TBInfo CMD chain into the player-typed keywords for a remoteaction
// directive. Sibling to TBInfoTeleportResolver — same Action-string parse shape,
// different terminal directive.
//
// A remoteaction line looks like this (verified against the v1.11p data for map
// 9 / room 1012, CMD 1422):
//
//     clear rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//     move rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//     push rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//
// Each line's first colon-separated token is the keyword the player types. The
// middle testskill may gate success on a stat check; the final remoteaction
// describes what changes in the world when the check passes. For the hover
// tooltip we only care about surfacing the keyword candidates — the user wants
// to know what to type. The walker's prerequisite-action expander owns the full
// semantics (skill check + remote-action firing).
public static class TBInfoActionResolver
{
    // Yields the player-typed keyword from each line in the CMD's Action chain
    // that ends with a remoteaction directive. Skips lines without such a
    // directive (teleport / message-only branches are not action
    // prerequisites). Duplicates preserved because the order in the Action chain
    // may be meaningful to the user (recognised verbs first, synonyms second).
    public static IEnumerable<string> EnumerateRemoteActionKeywords(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            bool hasRemote = false;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("remoteaction", StringComparison.OrdinalIgnoreCase))
                {
                    hasRemote = true;
                    break;
                }
            }
            if (hasRemote) yield return keyword;
        }
    }

    // Yields the player-typed keyword from each line whose directive chain performs
    // an in-place effect — a `giveitem` or `random` directive — the mine / gather
    // commands in the Dwarven Mines ("mine ore", "mine vein", …, which yield ore
    // via a checkitem/testskill gate). Lines that teleport, cast, or fire a
    // remoteaction are surfaced by their own resolvers (TBInfoTeleportResolver /
    // TBInfoCastTeleportResolver / EnumerateRemoteActionKeywords), so they're
    // excluded here to keep a keyword from being listed twice. Order preserved
    // (recognised verbs first, synonyms second); duplicates left for the caller to
    // dedupe.
    public static IEnumerable<string> EnumerateRoomActionKeywords(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            bool yieldsItem = false;
            bool routedElsewhere = false;
            for (int i = 1; i < parts.Length; i++)
            {
                string directive = parts[i];
                if (directive.StartsWith("giveitem", StringComparison.OrdinalIgnoreCase)
                 || directive.StartsWith("random", StringComparison.OrdinalIgnoreCase))
                    yieldsItem = true;
                else if (directive.StartsWith("teleport", StringComparison.OrdinalIgnoreCase)
                      || directive.StartsWith("cast", StringComparison.OrdinalIgnoreCase)
                      || directive.StartsWith("remoteaction", StringComparison.OrdinalIgnoreCase))
                    routedElsewhere = true;
            }
            if (yieldsItem && !routedElsewhere) yield return keyword;
        }
    }

    // One paid room command: the player keyword, the highest `price` it charges
    // (in copper, the base coin), and whether the line carries several DISTINCT
    // escalating prices. The tiered case is the jail "bribe guard" — its six
    // powers-of-ten prices mean "the guard takes the largest tier you can afford,
    // up to the max" (confirmed by the user), so only the ceiling is meaningful.
    public readonly record struct PricedCommand(string Keyword, long MaxCopper, bool Tiered);

    // Yields the paid commands in a room's CMD chain — every keyword line that
    // carries at least one `price <copper> [failTextblock]` directive (gambling,
    // healer / summon buys, passage fares, bribe guard). The first integer after
    // `price` is the copper cost; a second integer is the can't-afford textblock,
    // not a cost. MaxCopper is the largest price on the line; Tiered is set when
    // the line lists more than one distinct price.
    public static IEnumerable<PricedCommand> EnumeratePricedCommands(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            long max = 0;
            var distinct = new HashSet<long>();
            for (int i = 1; i < parts.Length; i++)
            {
                if (!parts[i].StartsWith("price", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryFirstAmount(parts[i], out long copper) && copper > 0)
                {
                    distinct.Add(copper);
                    if (copper > max) max = copper;
                }
            }
            if (distinct.Count == 0) continue;
            yield return new PricedCommand(keyword, max, distinct.Count > 1);
        }
    }

    // First whole number in a `price 100 1560` token (skips the `price` word,
    // returns 100 — the copper cost, not the trailing fail-textblock id).
    private static bool TryFirstAmount(string token, out long value)
    {
        foreach (string word in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(word, out value)) return true;
        value = 0;
        return false;
    }
}
