using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Map;

// Expected-exp read of a room's monster-summoning entry spell. A room's Spell
// (Rooms.Spell) can carry a TextBlock ability (Spells Abil code 148) whose action is
// a d100 roll table that summons monsters — cast on room entry and re-rolled on the
// room-spell tick while you're in the room. Parsed from the TBInfo action chain so the
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
//
// One line of the table that summons: every monster it brings (a line can name
// several), the chance the roll lands on it, their exp together, and whether it runs
// only in a room with no monsters (its own `nomonsters`, or the leading block's).
public readonly record struct RoomSummonEntry(
    IReadOnlyList<int> Monsters, double Probability, int Exp, bool EmptyRoomOnly);

// A summon line the table names and the estimate doesn't count, kept so the log can
// say what was left out and why.
public readonly record struct RoomSummonLeftOut(int Threshold, IReadOnlyList<int> Monsters, string Reason);

// ExpPerRoll = Σ (line% × its monsters' exp) — the probability-weighted exp of one
// roll; EmptyRoomExpPerRoll is the share of it on lines that run only in an empty
// room. SummonChance = Σ line% — the chance any monster is summoned. NoMonstersGate
// mirrors a "nomonsters:" on the block that leads to the table (the whole spell only
// fires in an empty room).
public sealed record RoomSummonTable(
    double ExpPerRoll, double EmptyRoomExpPerRoll, double SummonChance, bool NoMonstersGate,
    IReadOnlyList<RoomSummonEntry> Entries);

public static class RoomSummonParser
{
    public const string NeedsRoomItem = "needs an item in the room";
    public const string OneAtATimeBoss = "one-at-a-time boss";

    // Follow at most this many `random N` redirects before giving up (guards a
    // malformed / self-referential chain).
    private const int MaxRedirects = 6;

    // Resolve a spell's TextBlock into its summon roll table, or null when the block
    // summons nothing that counts (a message-only / teleport TextBlock, an empty
    // chain, or a table whose every summon is left out). tbAction maps a TBInfo
    // Number to its raw Action string; monster maps a monster Number to its exp and
    // whether only one of it can be alive in the game. leftOut, when given, receives
    // each summon line that wasn't counted.
    public static RoomSummonTable? Resolve(
        int textBlock, Func<int, string?> tbAction, Func<int, (int Exp, bool OneAtATime)> monster,
        ICollection<RoomSummonLeftOut>? leftOut = null)
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
            if (FirstTokenIsThreshold(act)) return ParseTable(act, noMonsters, monster, leftOut);
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
                if (IsStep(tok, "nomonsters")) noMonsters = true;
                else if (NumberAfter(tok, "random") is int n and > 0)
                    return n;
            }
        return 0;
    }

    // The lines of a d100 roll table, in order: each one's leading threshold and the
    // steps after it. A line that doesn't lead with a number isn't a band and is left
    // out. Shared with RoomSpellTeleportClassifier, which reads the same tables for
    // their teleports.
    internal static IEnumerable<(int Threshold, string[] Steps)> ReadBands(string table)
    {
        foreach (string raw in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int threshold)) continue;
            yield return (threshold, parts[1..]);
        }
    }

    // Parse a d100 roll table. Each line is "<cumulativeThreshold>:<act>[:<act>…]"; a
    // line's band probability is (threshold − prevThreshold) / lastThreshold, and the
    // band summons every monster its "summon <mon>" steps name.
    private static RoomSummonTable? ParseTable(
        string table, bool noMonsters, Func<int, (int Exp, bool OneAtATime)> monster,
        ICollection<RoomSummonLeftOut>? leftOut)
    {
        var lines = new List<(int Threshold, List<int> Monsters, bool EmptyRoomOnly)>();
        foreach ((int threshold, string[] steps) in ReadBands(table))
        {
            var counted = new List<int>();
            var behindRoomItem = new List<int>();
            bool roomItem = false, emptyRoomOnly = noMonsters;
            foreach (string step in steps)
            {
                if (IsStep(step, "roomitem") || IsStep(step, "failroomitem") || IsStep(step, "clearitem"))
                    roomItem = true;
                else if (IsStep(step, "nomonsters"))
                    // Steps run left to right, so only one ahead of the summons gates them.
                    emptyRoomOnly |= counted.Count == 0 && behindRoomItem.Count == 0;
                else if (NumberAfter(step, "summon") is int m and > 0)
                    (roomItem ? behindRoomItem : counted).Add(m);
            }

            // The table is read once for the spell, and which of its rooms hold an
            // item differs room to room: the graveyard's Death Shrieker line needs
            // the weeping statue 7 of its 110 rooms have, and takes it. Crediting it
            // to every room would swamp the estimate, so a summon behind a step that
            // asks about a room item counts for nothing (user, 2026-10-09).
            if (behindRoomItem.Count > 0)
                leftOut?.Add(new RoomSummonLeftOut(threshold, behindRoomItem, NeedsRoomItem));

            // A boss only one of which can be alive comes once per its regen however
            // often the line is rolled, so every roll would be credited a kill that
            // happens once. Lines with one are left out of estimates altogether
            // (user, 2026-10-09), the whole line with it.
            if (counted.Any(m => monster(m).OneAtATime))
            {
                leftOut?.Add(new RoomSummonLeftOut(threshold, counted, OneAtATimeBoss));
                counted = new List<int>();
            }
            lines.Add((threshold, counted, emptyRoomOnly));
        }
        if (lines.Count == 0) return null;

        double denom = lines[^1].Threshold;   // the top of the d100 band (usually 100)
        if (denom <= 0) return null;

        double expPerRoll = 0, emptyRoomExp = 0, chance = 0;
        var entries = new List<RoomSummonEntry>();
        int prev = 0;
        foreach ((int threshold, List<int> monsters, bool emptyRoomOnly) in lines)
        {
            double prob = Math.Max(0, threshold - prev) / denom;
            prev = threshold;
            if (monsters.Count == 0) continue;
            int exp = monsters.Sum(m => monster(m).Exp);
            expPerRoll += prob * exp;
            if (emptyRoomOnly) emptyRoomExp += prob * exp;
            chance += prob;
            entries.Add(new RoomSummonEntry(monsters, prob, exp, emptyRoomOnly));
        }
        return entries.Count > 0 ? new RoomSummonTable(expPerRoll, emptyRoomExp, chance, noMonsters, entries) : null;
    }

    // A step is its leading word and then its arguments (`nomonsters`, `nomonsters 837`).
    private static bool IsStep(string step, string word) =>
        step.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (step.Length == word.Length || step[word.Length] == ' ');

    // The number a `summon` or `random` step names, or null when the step is another one.
    private static int? NumberAfter(string step, string word) =>
        IsStep(step, word) && step.Length > word.Length
        && int.TryParse(step.AsSpan(word.Length).Trim(), out int n) ? n : null;
}
