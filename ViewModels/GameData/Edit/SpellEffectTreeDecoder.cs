using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Decodes a room spell's effect TextBlock (its Abil-148 record) into the expandable
// effect tree shown on the spell's Game Data tab. Two block shapes recur:
//   • a CONDITIONAL block whose lines lead with gates (minlevel/maxlevel/checkitem/
//     failitem) then a terminal random/effect — each becomes a tinted branch header;
//   • a WEIGHTED block whose lines lead with a cumulative threshold — each becomes an
//     outcome with its own percentage.
// Effects resolve to linked runs (summon → Monster record, cast → Spell record,
// teleport → map room). A random block that is nothing but teleports collapses to a
// one-line summary ("a random room — N destinations") so a 100-entry sweep table
// doesn't flood the tree. Pure read of the active set's tables; cycle- and depth-
// guarded. AppServices openers are touched only inside deferred link commands.
public static class SpellEffectTreeDecoder
{
    private const int MaxDepth = 7;
    // A random block with at least this many teleport-only outcomes collapses to a
    // summary line instead of listing every destination.
    private const int TeleportCollapseThreshold = 4;

    private static readonly HashSet<string> ConditionVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "minlevel", "maxlevel", "checkitem", "failitem", "checkability", "class", "race" };

    public static IReadOnlyList<SpellEffectNode> Decode(GameDataCache cache, int textblockNumber)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (textblockNumber <= 0) return Array.Empty<SpellEffectNode>();
        if (cache.GetRawTable("TBInfo") is not { } tbDoc) return Array.Empty<SpellEffectNode>();

        var tb = new Dictionary<int, JsonElement>();
        foreach (JsonElement e in tbDoc.RootElement.EnumerateArray())
        {
            int n = ReadInt(e, "Number");
            if (n > 0) tb.TryAdd(n, e);
        }
        var ctx = new Ctx(cache, tb, BuildRoomNames(cache));
        return DecodeBlock(ctx, textblockNumber, 0, new HashSet<int>());
    }

    private sealed record Ctx(GameDataCache Cache, Dictionary<int, JsonElement> Tb, Dictionary<(int, int), string> RoomNames);

    private static IReadOnlyList<SpellEffectNode> DecodeBlock(Ctx ctx, int tbNum, int depth, HashSet<int> ancestors)
    {
        if (tbNum <= 0 || depth > MaxDepth || !ancestors.Add(tbNum)) return Array.Empty<SpellEffectNode>();
        try
        {
            if (!ctx.Tb.TryGetValue(tbNum, out JsonElement entry)) return Array.Empty<SpellEffectNode>();
            string action = entry.TryGetProperty("Action", out JsonElement a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? string.Empty : string.Empty;

            var nodes = new List<SpellEffectNode>();
            int prevThreshold = 0;
            foreach (string rawLine in action.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                var cmds = new List<string>();
                foreach (string c in line.Split(':')) { string t = c.Trim(); if (t.Length > 0) cmds.Add(t); }
                if (cmds.Count == 0) continue;

                if (IsThreshold(cmds[0], out int threshold))
                {
                    int pct = Math.Max(0, threshold - prevThreshold);
                    prevThreshold = threshold;
                    nodes.Add(BuildOutcome(ctx, pct, cmds.GetRange(1, cmds.Count - 1), depth, ancestors));
                }
                else
                {
                    nodes.Add(BuildBranch(ctx, cmds, depth, ancestors));
                }
            }
            return nodes;
        }
        finally { ancestors.Remove(tbNum); }
    }

    // A conditional line: leading gate commands form the (tinted) branch header; the
    // rest is the terminal effect / random rolled when the gate passes.
    private static SpellEffectNode BuildBranch(Ctx ctx, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        var conditions = new List<string>();
        var itemRuns = new List<MdbInline>();
        bool hasFail = false, hasCheck = false;
        int splitAt = 0;
        for (; splitAt < cmds.Count; splitAt++)
        {
            (string verb, string[] args) = Parse(cmds[splitAt]);
            if (!ConditionVerbs.Contains(verb)) break;
            switch (verb)
            {
                case "minlevel": conditions.Add($"level ≥ {Arg(args, 0)}"); break;
                case "maxlevel": conditions.Add($"level ≤ {Arg(args, 0)}"); break;
                case "checkitem": hasCheck = true; conditions.Add("carrying " + ItemName(ctx, Arg(args, 0))); break;
                case "failitem": hasFail = true; conditions.Add("not carrying " + ItemName(ctx, Arg(args, 0))); break;
                default: conditions.Add(verb + " " + string.Join(" ", args)); break;
            }
        }

        var terminal = cmds.GetRange(splitAt, cmds.Count - splitAt);
        IReadOnlyList<SpellEffectNode> children = BuildTerminalChildren(ctx, terminal, depth, ancestors);

        string tone = hasFail && !hasCheck ? "danger" : hasCheck ? "accent" : "neutral";
        return new SpellEffectNode
        {
            Runs = new[] { new MdbInline(conditions.Count > 0 ? string.Join(", ", conditions) : "on entry") },
            Children = children,
            IsBranch = true,
            Tone = tone,
            StartExpanded = true,
        };
    }

    // The terminal of a branch (after its gates): a random block → its weighted
    // outcomes as children; otherwise a single effect line.
    private static IReadOnlyList<SpellEffectNode> BuildTerminalChildren(Ctx ctx, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        int random = RandomTarget(cmds);
        if (random > 0)
        {
            if (TryCollapseTeleports(ctx, random) is { } summary)
                return new[] { new SpellEffectNode { Runs = summary } };
            return DecodeBlock(ctx, random, depth + 1, ancestors);
        }
        (IReadOnlyList<MdbInline> runs, _) = BuildEffectRuns(ctx, cmds);
        return runs.Count > 0 ? new[] { new SpellEffectNode { Runs = runs } } : Array.Empty<SpellEffectNode>();
    }

    // A weighted outcome: its percentage plus either a nested random (children) or a
    // direct effect line.
    private static SpellEffectNode BuildOutcome(Ctx ctx, int pct, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        int random = RandomTarget(cmds);
        (IReadOnlyList<MdbInline> runs, bool nothing) = BuildEffectRuns(ctx, cmds);

        if (random > 0)
        {
            if (TryCollapseTeleports(ctx, random) is { } summary)
            {
                var merged = new List<MdbInline>(runs);
                merged.AddRange(summary);
                return new SpellEffectNode { Percent = pct, Runs = merged };
            }
            return new SpellEffectNode
            {
                Percent = pct,
                Runs = runs,
                Children = DecodeBlock(ctx, random, depth + 1, ancestors),
                StartExpanded = depth < 1,
            };
        }

        if (runs.Count == 0 || nothing)
            return new SpellEffectNode { Percent = pct, Runs = new[] { new MdbInline("nothing") } };
        return new SpellEffectNode { Percent = pct, Runs = runs };
    }

    // Build the display runs for a set of effect commands (summon/cast/teleport/
    // nomonsters/addexp), resolving names to links. Consecutive identical summons
    // collapse to "×N". Returns whether the only content was a do-nothing (addexp 0).
    private static (IReadOnlyList<MdbInline> Runs, bool Nothing) BuildEffectRuns(Ctx ctx, List<string> cmds)
    {
        var runs = new List<MdbInline>();
        bool sawSomething = false, sawNothing = false;

        int i = 0;
        while (i < cmds.Count)
        {
            (string verb, string[] args) = Parse(cmds[i]);
            switch (verb)
            {
                case "summon":
                {
                    int mon = ArgInt(args, 0);
                    int count = 1;
                    while (i + 1 < cmds.Count && Parse(cmds[i + 1]) is var (v2, a2) && v2 == "summon" && ArgInt(a2, 0) == mon)
                    { count++; i++; }
                    AddSep(runs);
                    runs.Add(MonsterLink(ctx, mon));
                    if (count > 1) runs.Add(new MdbInline($" ×{count}"));
                    sawSomething = true;
                    break;
                }
                case "cast":
                    AddSep(runs);
                    runs.Add(new MdbInline("casts "));
                    runs.Add(SpellLink(ctx, ArgInt(args, 0)));
                    sawSomething = true;
                    break;
                case "teleport":
                {
                    int room = ArgInt(args, 0), map = ArgInt(args, 1);
                    AddSep(runs);
                    runs.Add(new MdbInline("→ "));
                    runs.Add(RoomLink(ctx, map, room));
                    sawSomething = true;
                    break;
                }
                case "nomonsters":
                    AddSep(runs);
                    runs.Add(new MdbInline("clears the room"));
                    sawSomething = true;
                    break;
                case "addexp":
                    if (ArgInt(args, 0) == 0) sawNothing = true;
                    break;
            }
            i++;
        }
        return (runs, !sawSomething && sawNothing);
    }

    // If a random block is all teleports (a sweep / crossing table), return a one-line
    // summary instead of listing every destination. Null when it isn't collapsible.
    private static IReadOnlyList<MdbInline>? TryCollapseTeleports(Ctx ctx, int tbNum)
    {
        if (!ctx.Tb.TryGetValue(tbNum, out JsonElement entry)) return null;
        string action = entry.TryGetProperty("Action", out JsonElement a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() ?? string.Empty : string.Empty;

        var dests = new List<(int Map, int Room)>();
        foreach (string rawLine in action.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            var cmds = new List<string>();
            foreach (string c in line.Split(':')) { string t = c.Trim(); if (t.Length > 0) cmds.Add(t); }
            if (cmds.Count < 2 || !IsThreshold(cmds[0], out _)) return null;   // not a weighted block
            (string verb, string[] args) = Parse(cmds[1]);
            if (cmds.Count != 2 || verb != "teleport") return null;            // not teleport-only
            dests.Add((ArgInt(args, 1), ArgInt(args, 0)));
        }
        if (dests.Count < TeleportCollapseThreshold) return null;

        string? common = ctx.RoomNames.TryGetValue(dests[0], out string? n0) ? n0 : null;
        foreach ((int m, int r) in dests)
            if (!ctx.RoomNames.TryGetValue((m, r), out string? nm) || nm != common) { common = null; break; }

        string count = dests.Count.ToString(CultureInfo.InvariantCulture);
        if (common is { Length: > 0 })
        {
            var runs = new List<MdbInline> { new("→ ") };
            runs.Add(RoomLink(ctx, dests[0].Map, dests[0].Room, common));
            runs.Add(new MdbInline($" ({count} rooms)"));
            return runs;
        }
        return new[] { new MdbInline($"→ a random room ({count} destinations)") };
    }

    // ----- link runs -----
    private static MdbInline MonsterLink(Ctx ctx, int number)
    {
        string name = number > 0 ? ctx.Cache.FindNameByNumber("Monsters", number) ?? $"monster #{number}" : "monster";
        return number > 0
            ? new MdbInline(name, new RelayCommand(() => AppServices.Current.OpenMonsterGameData(number)))
            : new MdbInline(name);
    }

    private static MdbInline SpellLink(Ctx ctx, int number)
    {
        string name = number > 0 ? ctx.Cache.FindNameByNumber("Spells", number) ?? $"spell #{number}" : "spell";
        return number > 0
            ? new MdbInline(name, new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(number)))
            : new MdbInline(name);
    }

    private static MdbInline RoomLink(Ctx ctx, int map, int room, string? name = null)
    {
        string label = name
            ?? (ctx.RoomNames.TryGetValue((map, room), out string? n) ? n
                : $"{map.ToString(CultureInfo.InvariantCulture)}/{room.ToString(CultureInfo.InvariantCulture)}");
        if (map <= 0 || room <= 0) return new MdbInline(label);
        var key = new Game.Map.RoomKey(map, room);
        return new MdbInline(label, new RelayCommand(() => AppServices.Current.NavigateToRoom(key)));
    }

    private static string ItemName(Ctx ctx, string idText)
        => int.TryParse(idText, out int id) && id > 0
            ? ctx.Cache.FindNameByNumber("Items", id) ?? $"item #{id}"
            : idText;

    // ----- helpers -----
    private static void AddSep(List<MdbInline> runs) { if (runs.Count > 0) runs.Add(new MdbInline(", ")); }

    private static int RandomTarget(List<string> cmds)
    {
        foreach (string c in cmds)
        {
            (string verb, string[] args) = Parse(c);
            if (verb == "random") return ArgInt(args, 0);
        }
        return 0;
    }

    private static (string Verb, string[] Args) Parse(string cmd)
    {
        string[] tok = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tok.Length == 0 ? (string.Empty, Array.Empty<string>()) : (tok[0].ToLowerInvariant(), tok[1..]);
    }

    private static bool IsThreshold(string cmd, out int value)
        => int.TryParse(cmd.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string Arg(string[] args, int i) => i < args.Length ? args[i] : string.Empty;
    private static int ArgInt(string[] args, int i) => i < args.Length && int.TryParse(args[i], out int v) ? v : 0;

    private static Dictionary<(int, int), string> BuildRoomNames(GameDataCache cache)
    {
        var map = new Dictionary<(int, int), string>();
        if (cache.GetRawTable("Rooms") is not { } doc) return map;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            int m = ReadInt(r, "Map Number"), rm = ReadInt(r, "Room Number");
            if (m <= 0 || rm <= 0) continue;
            if (r.TryGetProperty("Name", out JsonElement e) && e.ValueKind == JsonValueKind.String
                && e.GetString() is { } nm && nm.Trim().Length > 0 && nm[0] != '\0')
                map[(m, rm)] = nm.Trim();
        }
        return map;
    }

    private static int ReadInt(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out int n) ? n : 0;
}
