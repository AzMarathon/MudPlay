using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Expected-exp read of a room's monster-summoning entry spell. A room's Spell
// (Rooms.Spell) can carry a TextBlock ability (Spells Abil code 148) whose action is
// a d100 roll table that summons monsters — cast on room entry and re-rolled every
// combat tick while you're in the room. Parsed from the TBInfo action chain so the
// Exp/Hr estimator can credit the extra kills these rooms hand you.
//
// Worked example — Paradigm "crypt summon 2" (spell 5248 → TBInfo 3411):
//   #3411: "nomonsters:random 3412"           gate (only fires when the room is empty)
//   #3412: "60:addevil 0                       1–60   : nothing
//           85:message 4064                    61–85  : a message, no summon
//           90:message 4063:summon 2111        86–90  : summon cairn wraith
//           95:message 4063:summon 2119        91–95  : summon ogre skeleton
//           100:message 4063:summon 2122"      96–100 : summon zombie warrior
// → 15% summon chance; expected 1,850 exp per roll (0.05 × 13000/12000/12000).
public readonly record struct RoomSummonEntry(int Monster, string MonsterName, double Probability, int Exp);

// ExpPerRoll = Σ (band% × monster exp) — the probability-weighted exp of one roll.
// SummonChance = Σ band% — the chance any monster is summoned. NoMonstersGate mirrors
// the "nomonsters:" condition (the spell only fires in an empty room).
public sealed record RoomSummonTable(
    double ExpPerRoll, double SummonChance, bool NoMonstersGate, IReadOnlyList<RoomSummonEntry> Entries);

public static class RoomSummonParser
{
    // Follow at most this many `random N` redirects before giving up (guards a
    // malformed / self-referential chain).
    private const int MaxRedirects = 6;

    // Resolve a spell's TextBlock into its summon roll table, or null when the block
    // summons nothing (a message-only / teleport TextBlock, or an empty chain).
    // tbAction maps a TBInfo Number to its raw Action string; monster maps a monster
    // Number to (exp, name).
    public static RoomSummonTable? Resolve(
        int textBlock, Func<int, string?> tbAction, Func<int, (int Exp, string Name)> monster)
    {
        ArgumentNullException.ThrowIfNull(tbAction);
        ArgumentNullException.ThrowIfNull(monster);
        if (textBlock <= 0) return null;

        bool noMonsters = false;
        int cur = textBlock;
        var visited = new HashSet<int>();
        for (int hop = 0; hop < MaxRedirects && cur > 0 && visited.Add(cur); hop++)
        {
            string? act = tbAction(cur);
            if (string.IsNullOrWhiteSpace(act)) return null;
            // A roll table starts each line with a numeric threshold; a gate/redirect
            // block (e.g. "nomonsters:random 3412") does not — follow it to the table.
            if (FirstTokenIsThreshold(act)) return ParseTable(act, noMonsters, monster);
            int redirect = FindRandom(act, ref noMonsters);
            if (redirect <= 0) return null;   // neither a table nor a redirect
            cur = redirect;
        }
        return null;
    }

    // True when the block's first non-empty line begins with an integer threshold —
    // the shape of a d100 roll table ("90:message 4063:summon 2111").
    private static bool FirstTokenIsThreshold(string action)
    {
        foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = raw.IndexOf(':');
            string head = (colon < 0 ? raw : raw[..colon]).Trim();
            return head.Length > 0 && int.TryParse(head, out _);
        }
        return false;
    }

    // A `random N` token anywhere in a gate/redirect block points at the roll table;
    // a `nomonsters` token flips the empty-room gate. Returns 0 when there's no redirect.
    private static int FindRandom(string action, ref bool noMonsters)
    {
        foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            foreach (string tok in raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Equals("nomonsters", StringComparison.OrdinalIgnoreCase)) noMonsters = true;
                else if (tok.StartsWith("random ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tok.AsSpan("random ".Length).Trim(), out int n) && n > 0)
                    return n;
            }
        return 0;
    }

    // Parse a d100 roll table. Each line is "<cumulativeThreshold>:<act>[:<act>…]"; a
    // line's band probability is (threshold − prevThreshold) / lastThreshold, and if any
    // of its actions is "summon <mon>", that band summons the monster.
    private static RoomSummonTable? ParseTable(
        string table, bool noMonsters, Func<int, (int Exp, string Name)> monster)
    {
        var lines = new List<(int Threshold, int Summon)>();
        foreach (string raw in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int threshold)) continue;
            int summon = 0;
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].StartsWith("summon ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[i].AsSpan("summon ".Length).Trim(), out int m) && m > 0)
                {
                    summon = m;
                    break;
                }
            lines.Add((threshold, summon));
        }
        if (lines.Count == 0) return null;

        double denom = lines[^1].Threshold;   // the top of the d100 band (usually 100)
        if (denom <= 0) return null;

        double expPerRoll = 0, chance = 0;
        var entries = new List<RoomSummonEntry>();
        int prev = 0;
        foreach ((int threshold, int summon) in lines)
        {
            double prob = Math.Max(0, threshold - prev) / denom;
            prev = threshold;
            if (summon <= 0) continue;
            (int exp, string name) = monster(summon);
            expPerRoll += prob * exp;
            chance += prob;
            entries.Add(new RoomSummonEntry(summon, name, prob, exp));
        }
        return entries.Count > 0 ? new RoomSummonTable(expPerRoll, chance, noMonsters, entries) : null;
    }
}
