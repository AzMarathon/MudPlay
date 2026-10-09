using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// How a TBInfo Action directive relates the block to a quest flag.
public enum QuestFlagRelation { Grants, Advances, Requires, Tests, Gate, Clears }

// The record kind a flag reference roots at, via the block's Called-From chain.
public enum QuestFlagSourceKind { Monster, Room, Spell, Textblock }

// One (flag, relationship, source, script line) fact: a TBInfo directive touches a quest flag,
// attributed to the NPC / room / spell that reaches it. Value is the directive's second
// argument (give-step, required value, addability delta); 0 when the verb takes none
// (removeability), when it was absent, or for failability, whose second number is the message
// it prints. Line is the script line the directive sits on.
public readonly record struct QuestFlagRef(
    int Flag, string FlagName, QuestFlagRelation Relation, QuestFlagSourceKind SourceKind,
    int SourceNumber, int Map, int Room, string SourceName, int Value, QuestFlagLine Line);

// Lazy per-set index of every quest-flag reference in the active set's TBInfo table — the data
// behind the Game Data Browser's Quest Flags view and its Quest Flag Steps window. Mirrors
// ItemSourceIndex in shape: it scans each TBInfo Action line for the ability directives
// (give/add/check/test/fail/removeability), attributes each to its monster / room / spell root
// by walking the block's Called-From provenance, works out how the line is set off, and
// resolves ids to names via GameDataCache. Builds on first query and self-invalidates on a set
// swap by comparing the cache's ActiveSet.
public sealed class QuestFlagIndex
{
    private readonly GameDataCache _cache;
    // Serializes builds and guards the published-snapshot fields. The build runs on a
    // background thread (GameDataTableSectionViewModel.LoadAsync → Task.Run), and two
    // overlapping loads (tab re-activation, a set swap mid-load) would otherwise both
    // enter Rebuild — one clearing/adding the list while the other is mid-Sort, which
    // trips List.Sort's "IComparer returns inconsistent results" guard and crashes.
    private readonly object _gate = new();
    // Published atomically at the end of Rebuild under _gate: Entries hands its list out as
    // a live view, so everything is built into fresh collections and swapped in whole — never
    // mutated in place after a reader could be holding it.
    private Snapshot _snapshot = Snapshot.Empty;
    private string? _loadedSet;
    private bool _built;

    // Provenance-walk safety cap — a Called-From chain is a small acyclic tree in practice, but
    // the visited-set plus this bound keeps a malformed cycle from spinning.
    private const int MaxWalkSteps = 512;

    // Roots named in a line's Sources text before it is cut short.
    private const int MaxSourcesNamed = 4;
    private const int MaxCastByLength = 90;

    private static readonly Dictionary<string, QuestFlagRelation> s_verbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["giveability"]   = QuestFlagRelation.Grants,
            ["addability"]    = QuestFlagRelation.Advances,
            ["checkability"]  = QuestFlagRelation.Requires,
            ["testability"]   = QuestFlagRelation.Tests,
            ["failability"]   = QuestFlagRelation.Gate,
            ["removeability"] = QuestFlagRelation.Clears,
        };

    private sealed record Snapshot(
        List<QuestFlagRef> Refs,
        Dictionary<int, List<QuestFlagLine>> LinesByFlag,
        HashSet<int> GrantedFlags,
        QuestScriptNames? Names)
    {
        public static readonly Snapshot Empty = new(new(), new(), new(), null);
    }

    public QuestFlagIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // Every quest-flag reference in the active set, sorted flag → relationship → source →
    // script line. Live view — read, don't mutate.
    public IReadOnlyList<QuestFlagRef> Entries => EnsureBuilt().Refs;

    // The script lines that touch flag, in textblock then line order; empty when none do.
    public IReadOnlyList<QuestFlagLine> LinesFor(int flag)
        => EnsureBuilt().LinesByFlag.TryGetValue(flag, out List<QuestFlagLine>? lines)
            ? lines : Array.Empty<QuestFlagLine>();

    // True when some script in the set grants the ability with giveability — the rule the
    // quest crawl uses to tell a quest flag from a stat an addability raises.
    public bool IsQuestFlag(int ability) => EnsureBuilt().GrantedFlags.Contains(ability);

    // Names for the active set's record numbers; null when no set is loaded.
    public QuestScriptNames? Names => EnsureBuilt().Names;

    private Snapshot EnsureBuilt()
    {
        string? active = _cache.ActiveSet;
        lock (_gate)
        {
            if (_built && _loadedSet == active) return _snapshot;
            return Rebuild(active);
        }
    }

    private readonly record struct TbRow(string? Action, string? CalledFrom);

    // Caller holds _gate. Builds into fresh local collections, then publishes them whole — so
    // a concurrent reader (Entries) sees either the previous snapshot or this finished one,
    // never a list being cleared/appended/sorted underneath it.
    private Snapshot Rebuild(string? active)
    {
        Snapshot snapshot = Snapshot.Empty;
        JsonDocument? doc = string.IsNullOrWhiteSpace(active) ? null : _cache.GetRawTable("TBInfo");
        if (doc is not null)
        {
            // Number → (Action, Called-From) so the provenance walk can hop between blocks.
            Dictionary<int, TbRow> tb = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("Number", out JsonElement numEl)
                    || numEl.ValueKind != JsonValueKind.Number || !numEl.TryGetInt32(out int num))
                    continue;
                tb[num] = new TbRow(ReadString(el, "Action"), ReadString(el, "Called From"));
            }

            List<QuestFlagRef> refs = new();
            Dictionary<int, List<QuestFlagLine>> linesByFlag = new();
            HashSet<int> granted = QuestCrawler.DiscoverGrantedFlags(
                tb.Values.SelectMany(static row => (row.Action ?? string.Empty).Split('\n')));
            QuestScriptNames names = new(_cache);
            Dictionary<int, JsonElement> byNumber = QuestStepGraph.IndexByNumber(doc);
            IReadOnlyDictionary<int, IReadOnlyList<int>> deaths = QuestDeathSpells.For(_cache);

            HashSet<(int, QuestFlagRelation, QuestFlagSourceKind, int, int, int, int, int, int)> seen = new();
            List<SourceRoot> roots = new();
            foreach ((int number, TbRow row) in tb.OrderBy(static kv => kv.Key))
            {
                if (string.IsNullOrEmpty(row.Action)
                    || row.Action.IndexOf("ability", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                ResolveRoots(number, tb, roots);
                string[] rawLines = row.Action.Split('\n');
                // A textblock that repeats a line word for word adds nothing: the first
                // copy that passes ends the scan.
                HashSet<string> seenText = new(StringComparer.Ordinal);
                for (int order = 0; order < rawLines.Length; order++)
                {
                    string text = rawLines[order].Trim();
                    List<(QuestFlagRelation Rel, int Flag, int Value)> abilities = ParseAbilities(text);
                    if (abilities.Count == 0 || !seenText.Add(text)) continue;

                    QuestScriptLine script = QuestScriptLine.Parse(text);
                    List<QuestTrigger> triggers = ResolveTriggers(script, row.CalledFrom, roots, byNumber, deaths, names);
                    QuestFlagLine line = new(
                        number, order, script, CleanText(row.CalledFrom), triggers,
                        RollChance(script, rawLines), SourcesText(roots, names),
                        CommandText(triggers), script.LevelText,
                        string.Join(", ", script.Classes.Select(names.Class)),
                        string.Join(", ", script.Races.Select(names.Race)),
                        ItemsText(script, names));

                    foreach ((QuestFlagRelation rel, int flag, int value) in abilities)
                    {
                        if (!linesByFlag.TryGetValue(flag, out List<QuestFlagLine>? flagLines))
                            linesByFlag[flag] = flagLines = new List<QuestFlagLine>();
                        if (flagLines.Count == 0 || !ReferenceEquals(flagLines[^1], line)) flagLines.Add(line);

                        foreach (SourceRoot root in roots)
                        {
                            var key = (flag, rel, root.Kind, root.Number, root.Map, root.Room, value, number, order);
                            if (!seen.Add(key)) continue;
                            refs.Add(new QuestFlagRef(
                                flag, AbilityNames.FormatId(flag), rel, root.Kind,
                                root.Number, root.Map, root.Room, ResolveSourceName(root, names), value, line));
                        }
                    }
                }
            }

            refs.Sort(static (a, b) =>
            {
                int c = a.Flag.CompareTo(b.Flag);
                if (c != 0) return c;
                c = a.Relation.CompareTo(b.Relation);
                if (c != 0) return c;
                c = a.SourceKind.CompareTo(b.SourceKind);
                if (c != 0) return c;
                c = a.SourceNumber.CompareTo(b.SourceNumber);
                if (c != 0) return c;
                c = a.Map.CompareTo(b.Map);
                if (c != 0) return c;
                c = a.Room.CompareTo(b.Room);
                if (c != 0) return c;
                c = a.Line.Textblock.CompareTo(b.Line.Textblock);
                if (c != 0) return c;
                c = a.Line.Order.CompareTo(b.Line.Order);
                return c != 0 ? c : a.Value.CompareTo(b.Value);
            });

            snapshot = new Snapshot(refs, linesByFlag, granted, names);
        }

        _snapshot = snapshot;
        _loadedSet = active;
        _built = true;
        return snapshot;
    }

    // Every ability directive on one Action line. A line is a colon-delimited directive chain;
    // a token whose head verb is one of the ability verbs names a flag (first arg) and an
    // optional value (second arg).
    private static List<(QuestFlagRelation Rel, int Flag, int Value)> ParseAbilities(string line)
    {
        List<(QuestFlagRelation, int, int)> found = new();
        foreach (string rawTok in line.Split(':'))
        {
            string[] p = rawTok.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !s_verbs.TryGetValue(p[0], out QuestFlagRelation rel)) continue;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag)
                || flag <= 0)
                continue;
            int value = 0;
            // failability's second number is the message it prints when the character has
            // the ability, not a value of the flag.
            if (p.Length >= 3 && rel != QuestFlagRelation.Gate)
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            found.Add((rel, flag, value));
        }
        return found;
    }

    // ----- How a line is set off -----

    // A line that opens with a typed command belongs to the room (typed as written) or the NPC
    // (asked as a keyword) its textblock hangs off. A line that opens with a directive is
    // reached through an NPC's keyword dispatch, or — when its chain starts at a spell some
    // monster's death casts — by killing that monster. Anything else is left unresolved.
    private static List<QuestTrigger> ResolveTriggers(
        QuestScriptLine script, string? calledFrom, List<SourceRoot> roots,
        Dictionary<int, JsonElement> byNumber,
        IReadOnlyDictionary<int, IReadOnlyList<int>> deaths, QuestScriptNames names)
    {
        List<QuestTrigger> triggers = new();
        if (script.Command is { } typed)
        {
            foreach (CalledFromRef r in ParseCalledFrom(calledFrom))
            {
                if (r.Kind == CfKind.Room)
                    triggers.Add(new QuestTrigger(QuestTriggerKind.RoomCommand, typed, Map: r.Map, Room: r.Room));
                else if (r.Kind == CfKind.Monster)
                    triggers.Add(new QuestTrigger(QuestTriggerKind.Ask, AskCommand(names, r.Number, typed), r.Number));
            }
            return triggers;
        }

        foreach (CalledFromRef r in ParseCalledFrom(calledFrom))
        {
            if (r.Kind != CfKind.Textblock) continue;
            string parent = string.Create(CultureInfo.InvariantCulture, $"Textblock #{r.Number}");
            if (QuestStepGraph.ResolveAskKeywords(parent, byNumber) is not { } hit) continue;
            if (triggers.Exists(t => t.Monster == hit.Monster)) continue;
            triggers.Add(new QuestTrigger(QuestTriggerKind.Ask, AskCommand(names, hit.Monster, hit.Keywords[0]), hit.Monster)
            {
                OtherKeywords = hit.Keywords.Skip(1).ToArray(),
            });
        }

        foreach (SourceRoot root in roots)
        {
            if (root.Kind != QuestFlagSourceKind.Spell
                || !deaths.TryGetValue(root.Number, out IReadOnlyList<int>? monsters))
                continue;
            foreach (int monster in monsters)
            {
                if (triggers.Exists(t => t.Kind == QuestTriggerKind.Kill && t.Monster == monster)) continue;
                triggers.Add(new QuestTrigger(
                    QuestTriggerKind.Kill, "kill " + names.Monster(monster).ToLowerInvariant(), monster));
            }
        }
        return triggers;
    }

    // The NPC's full name always works as the ask target.
    private static string AskCommand(QuestScriptNames names, int monster, string keyword)
        => $"ask {names.Monster(monster).ToLowerInvariant()} {keyword}";

    // The table's Command cell: each distinct way to set the line off.
    private static string CommandText(List<QuestTrigger> triggers)
    {
        List<string> parts = new();
        foreach (QuestTrigger t in triggers)
        {
            string text = t.OtherKeywords.Count > 0
                ? $"{t.Command} (or {string.Join(", ", t.OtherKeywords)})"
                : t.Command;
            if (!parts.Contains(text)) parts.Add(text);
        }
        return string.Join(" / ", parts);
    }

    private static string ItemsText(QuestScriptLine script, QuestScriptNames names)
    {
        List<string> parts = new();
        foreach (int item in script.HeldItems)
            if (!script.TakenItems.Contains(item)) parts.Add(names.Item(item));
        foreach (int item in script.TakenItems)
            parts.Add($"{names.Item(item)} (taken)");
        return string.Join(", ", parts);
    }

    private string SourcesText(List<SourceRoot> roots, QuestScriptNames names)
    {
        List<string> parts = new();
        foreach (SourceRoot root in roots)
        {
            if (root.Kind == QuestFlagSourceKind.Textblock) continue;
            if (parts.Count == MaxSourcesNamed)
            {
                parts.Add($"+{roots.Count - MaxSourcesNamed} more");
                break;
            }
            string name = ResolveSourceName(root, names);
            parts.Add(root.Kind switch
            {
                QuestFlagSourceKind.Room    => $"room {name} ({root.Map}/{root.Room})",
                QuestFlagSourceKind.Monster => $"monster {name} (#{root.Number})",
                _                           => $"spell {name} (#{root.Number}{CastBy(root.Number)})",
            });
        }
        return string.Join(", ", parts);
    }

    // What the Spells table lists as casting a spell, word for word, so a script that hangs
    // off a spell no monster's death casts still says where it comes from.
    private string CastBy(int spell)
    {
        if (_cache.FindRowByNumber("Spells", spell) is not { } row) return string.Empty;
        string text = CleanText(ReadString(row, "Casted By")).TrimEnd('+', ',', ' ');
        if (text.Length == 0) return string.Empty;
        if (text.Length > MaxCastByLength) text = text[..MaxCastByLength].TrimEnd(',', ' ') + " …";
        return $"; the data lists it as cast by {text}";
    }

    // A random table's lines are `threshold:steps` with running thresholds, so a line's share
    // of the draws is its threshold less the one before it, over the table's top threshold.
    private static double? RollChance(QuestScriptLine script, string[] blockLines)
    {
        if (script.RollBand is not int band) return null;
        int previous = 0, top = 0;
        foreach (string raw in blockLines)
        {
            int colon = raw.IndexOf(':');
            string lead = (colon < 0 ? raw : raw[..colon]).Trim();
            if (!int.TryParse(lead, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold)) continue;
            top = Math.Max(top, threshold);
            if (threshold < band) previous = Math.Max(previous, threshold);
        }
        return top > 0 ? (band - previous) * 100.0 / top : null;
    }

    // The importer pads some text fields with NULs.
    private static string CleanText(string? text) => (text ?? string.Empty).Replace("\0", string.Empty).Trim();

    // ----- Called-From provenance walk -----
    //
    // A deliberately small copy of the Called-From grammar ItemSourceIndex owns, per the same
    // convention (each consumer parses the provenance subset it needs rather than sharing an
    // exported parser). Unlike the item walk, a Spell root is reported (a flag set/checked by a
    // cast is a real source), not dead-ended.

    private enum CfKind { Monster, Room, Spell, Textblock }
    private readonly record struct CalledFromRef(CfKind Kind, int Number, int Map, int Room);
    private readonly record struct SourceRoot(QuestFlagSourceKind Kind, int Number, int Map, int Room);

    // Walk the block's Called-From graph upward, collecting the distinct monster / room / spell
    // roots. Textblock refs recurse. When nothing roots (an orphan or a textblock-only chain),
    // attribute to the originating block itself so the flag reference still surfaces.
    private static void ResolveRoots(int startNumber, Dictionary<int, TbRow> tb, List<SourceRoot> roots)
    {
        roots.Clear();
        if (!tb.TryGetValue(startNumber, out TbRow start)) return;

        HashSet<int> seenTb = new() { startNumber };
        Queue<TbRow> pending = new();
        pending.Enqueue(start);

        int steps = 0;
        while (pending.Count > 0 && steps++ < MaxWalkSteps)
        {
            TbRow block = pending.Dequeue();
            foreach (CalledFromRef r in ParseCalledFrom(block.CalledFrom))
            {
                switch (r.Kind)
                {
                    case CfKind.Monster:
                        AddRoot(roots, new SourceRoot(QuestFlagSourceKind.Monster, r.Number, 0, 0));
                        break;
                    case CfKind.Room:
                        AddRoot(roots, new SourceRoot(QuestFlagSourceKind.Room, 0, r.Map, r.Room));
                        break;
                    case CfKind.Spell:
                        AddRoot(roots, new SourceRoot(QuestFlagSourceKind.Spell, r.Number, 0, 0));
                        break;
                    case CfKind.Textblock:
                        if (r.Number > 0 && seenTb.Add(r.Number) && tb.TryGetValue(r.Number, out TbRow parent))
                            pending.Enqueue(parent);
                        break;
                }
            }
        }

        if (roots.Count == 0)
            roots.Add(new SourceRoot(QuestFlagSourceKind.Textblock, startNumber, 0, 0));
    }

    private static void AddRoot(List<SourceRoot> roots, SourceRoot root)
    {
        if (!roots.Contains(root)) roots.Add(root);
    }

    // Comma-separated provenance tokens: "Monster #61", "Room 7/1008", "Textblock #349",
    // "Textblock(rndm) #868", "Spell #559". Unknown / malformed tokens are skipped.
    private static IEnumerable<CalledFromRef> ParseCalledFrom(string? calledFrom)
    {
        if (string.IsNullOrWhiteSpace(calledFrom)) yield break;
        foreach (string raw in calledFrom.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            if (part.StartsWith("Monster", StringComparison.OrdinalIgnoreCase))
            {
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Monster, n, 0, 0);
            }
            else if (part.StartsWith("Room", StringComparison.OrdinalIgnoreCase))
            {
                if (TryRoom(part, out int map, out int room))
                    yield return new CalledFromRef(CfKind.Room, 0, map, room);
            }
            else if (part.StartsWith("Textblock", StringComparison.OrdinalIgnoreCase))
            {
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Textblock, n, 0, 0);
            }
            else if (part.StartsWith("Spell", StringComparison.OrdinalIgnoreCase))
            {
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Spell, n, 0, 0);
            }
        }
    }

    private static string ResolveSourceName(SourceRoot root, QuestScriptNames names) => root.Kind switch
    {
        QuestFlagSourceKind.Monster => names.Monster(root.Number),
        QuestFlagSourceKind.Spell   => names.Spell(root.Number),
        QuestFlagSourceKind.Room    => names.Room(root.Map, root.Room),
        _                           => $"Textblock #{root.Number}",
    };

    private static string? ReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int IntAfterHash(string s)
    {
        int hash = s.IndexOf('#');
        return hash < 0 ? 0 : LeadingInt(s[(hash + 1)..]);
    }

    private static bool TryRoom(string s, out int map, out int room)
    {
        map = 0; room = 0;
        int slash = s.IndexOf('/');
        if (slash < 0) return false;
        map = TrailingInt(s[..slash]);
        room = LeadingInt(s[(slash + 1)..]);
        return map > 0 && room > 0;
    }

    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start
            && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : 0;
    }

    private static int TrailingInt(string s)
    {
        int i = s.Length;
        while (i > 0 && s[i - 1] == ' ') i--;
        int end = i;
        while (i > 0 && s[i - 1] is >= '0' and <= '9') i--;
        return end > i
            && int.TryParse(s.AsSpan(i, end - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : 0;
    }
}
