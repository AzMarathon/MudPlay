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
                    nodes.Add(BuildSequenceNode(ctx, pct, cmds.GetRange(1, cmds.Count - 1), depth, ancestors));
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

    // A conditional line: its LEADING gate commands form the (tinted) branch header;
    // the rest is the terminal effect / random rolled when the gate passes.
    private static SpellEffectNode BuildBranch(Ctx ctx, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        int i = 0;
        var gates = new List<string>();
        bool hasFail = false, hasCheck = false;
        while (i < cmds.Count && TryGate(ctx, cmds[i], out string phrase))
        {
            (string verb, _) = Parse(cmds[i]);
            if (verb == "failitem") hasFail = true;
            else if (verb == "checkitem") hasCheck = true;
            gates.Add(phrase);
            i++;
        }
        string tone = hasFail && !hasCheck ? "danger" : hasCheck ? "accent" : "neutral";
        var rest = cmds.GetRange(i, cmds.Count - i);
        return new SpellEffectNode
        {
            Runs = new[] { new MdbInline(gates.Count > 0 ? string.Join(", ", gates) : "on entry") },
            Children = BuildTerminalChildren(ctx, rest, depth, ancestors),
            IsBranch = true,
            Tone = tone,
            IsExpanded = true,
        };
    }

    // The terminal of a branch (after its leading gates): a bare `random` redirects
    // straight to that block's weighted outcomes (no wrapper node); anything else is a
    // single ordered sequence node.
    private static IReadOnlyList<SpellEffectNode> BuildTerminalChildren(Ctx ctx, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        if (cmds.Count == 0) return Array.Empty<SpellEffectNode>();
        if (cmds.Count == 1 && Parse(cmds[0]).Verb == "random")
        {
            int random = ArgInt(Parse(cmds[0]).Args, 0);
            if (TryCollapseTeleports(ctx, random) is { } tele)
                return new[] { new SpellEffectNode { Runs = tele.Summary, Children = tele.Rooms, IsExpanded = false } };
            return DecodeBlock(ctx, random, depth + 1, ancestors);
        }
        return new[] { BuildSequenceNode(ctx, null, cmds, depth, ancestors) };
    }

    // A weighted outcome — or any effect sequence — built in COMMAND ORDER. Leading
    // gates prefix this node; effects up to the next gate are its own line; a `random`
    // redirect fills its children; and a gate that appears AFTER an effect nests the
    // remainder as a child (the game runs the line top-down and a failed gate aborts the
    // rest, so `summon A : nomonsters : summon B` means "A always, then B only if the
    // room is empty" — not "A and B when empty").
    private static SpellEffectNode BuildSequenceNode(Ctx ctx, int? pct, List<string> cmds, int depth, HashSet<int> ancestors)
    {
        int i = 0;
        var gates = new List<string>();
        while (i < cmds.Count && TryGate(ctx, cmds[i], out string phrase)) { gates.Add(phrase); i++; }

        var effectCmds = new List<string>();
        int random = 0;
        for (; i < cmds.Count; i++)
        {
            if (TryGate(ctx, cmds[i], out _)) break;   // a gate after effects → nested tail child
            (string verb, string[] args) = Parse(cmds[i]);
            if (verb == "random") random = ArgInt(args, 0);
            else effectCmds.Add(cmds[i]);
        }
        int tailStart = i;

        IReadOnlyList<MdbInline> effectRuns = BuildEffectRuns(ctx, effectCmds);
        var runs = new List<MdbInline>();
        if (gates.Count > 0) runs.Add(new MdbInline($"if {string.Join(", ", gates)} — "));
        runs.AddRange(effectRuns);

        var children = new List<SpellEffectNode>();
        bool collapsedRandom = false, collapsedTeleport = false;
        if (random > 0)
        {
            if (TryCollapseTeleports(ctx, random) is { } tele)
            {
                // Summary on this line; the individual rooms become hidden children so
                // the "N destinations" can be expanded to see them.
                runs.AddRange(tele.Summary);
                children.AddRange(tele.Rooms);
                collapsedRandom = true;
                collapsedTeleport = true;
            }
            else children.AddRange(DecodeBlock(ctx, random, depth + 1, ancestors));
        }
        if (tailStart < cmds.Count)
            children.Add(BuildSequenceNode(ctx, null, cmds.GetRange(tailStart, cmds.Count - tailStart), depth, ancestors));

        if (effectRuns.Count == 0 && !collapsedRandom && children.Count == 0)
            runs.Add(new MdbInline("nothing"));

        // A collapsed room sweep starts closed even at the top level; other nodes follow
        // the shallow-open rule.
        bool expanded = !collapsedTeleport && depth < 1;
        return new SpellEffectNode { Percent = pct, Runs = runs, Children = children, IsExpanded = expanded };
    }

    // A gate command (level / carry / no-NPCs) and its human phrase, or false for an
    // effect command. `nomonsters` is a CONDITION — the line fires only when the room
    // holds no NPCs — not an effect that clears the room.
    private static bool TryGate(Ctx ctx, string cmd, out string phrase)
    {
        (string verb, string[] args) = Parse(cmd);
        switch (verb)
        {
            case "nomonsters": phrase = "no NPCs in the room"; return true;
            case "minlevel": phrase = $"level ≥ {Arg(args, 0)}"; return true;
            case "maxlevel": phrase = $"level ≤ {Arg(args, 0)}"; return true;
            case "checkitem": phrase = "carrying " + ItemName(ctx, Arg(args, 0)); return true;
            case "failitem": phrase = "not carrying " + ItemName(ctx, Arg(args, 0)); return true;
        }
        if (ConditionVerbs.Contains(verb))
        {
            phrase = args.Length > 0 ? $"{verb} {string.Join(" ", args)}" : verb;
            return true;
        }
        phrase = string.Empty;
        return false;
    }

    // Build the display runs for a set of effect commands (summon / cast / teleport),
    // resolving names to links. Consecutive identical summons collapse to "×N".
    // Conditions (nomonsters / level / carry) are split out upstream by the sequence walk.
    private static IReadOnlyList<MdbInline> BuildEffectRuns(Ctx ctx, List<string> cmds)
    {
        var runs = new List<MdbInline>();
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
                    break;
                }
                case "cast":
                    AddSep(runs);
                    runs.Add(new MdbInline("casts "));
                    runs.Add(SpellLink(ctx, ArgInt(args, 0)));
                    break;
                case "teleport":
                {
                    int room = ArgInt(args, 0), map = ArgInt(args, 1);
                    AddSep(runs);
                    runs.Add(new MdbInline("→ "));
                    runs.Add(RoomLink(ctx, map, room));
                    break;
                }
            }
            i++;
        }
        return runs;
    }

    // If a random block is all teleports (a sweep / crossing table), collapse it to a
    // one-line summary whose hidden children are the individual destination rooms — so a
    // 99-room sweep reads as one line but still expands to every room. Null when the
    // block isn't a teleport-only table.
    private static (IReadOnlyList<MdbInline> Summary, IReadOnlyList<SpellEffectNode> Rooms)? TryCollapseTeleports(Ctx ctx, int tbNum)
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

        // The destinations are plain rooms (no per-room gate), so stack them as one
        // wrapped, comma-separated list of "name map/room" links — like the "Cast in
        // rooms" row — rather than a line per room. Hidden until the summary expands.
        var linkRuns = new List<MdbInline>();
        foreach ((int m, int r) in dests)
        {
            if (linkRuns.Count > 0) linkRuns.Add(new MdbInline(", "));
            linkRuns.Add(RoomLink(ctx, m, r, RoomLabel(ctx, m, r)));
        }
        var rooms = new[] { new SpellEffectNode { Runs = linkRuns } };

        string? common = ctx.RoomNames.TryGetValue(dests[0], out string? n0) ? n0 : null;
        foreach ((int m, int r) in dests)
            if (!ctx.RoomNames.TryGetValue((m, r), out string? nm) || nm != common) { common = null; break; }

        string count = dests.Count.ToString(CultureInfo.InvariantCulture);
        var summary = common is { Length: > 0 }
            ? new List<MdbInline> { new("→ "), RoomLink(ctx, dests[0].Map, dests[0].Room, common), new($" ({count} rooms)") }
            : new List<MdbInline> { new($"→ a random room ({count} destinations)") };
        return (summary, rooms);
    }

    // "name map/room" for a room, or bare "map/room" when the name is missing.
    private static string RoomLabel(Ctx ctx, int map, int room)
    {
        string mr = $"{map.ToString(CultureInfo.InvariantCulture)}/{room.ToString(CultureInfo.InvariantCulture)}";
        return ctx.RoomNames.TryGetValue((map, room), out string? n) && n.Length > 0 ? $"{n} {mr}" : mr;
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
