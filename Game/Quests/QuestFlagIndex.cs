using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
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
// it prints. Line is the script line the directive sits on; Command is how that line is set
// off from this row's own source (empty when the data doesn't establish it).
public readonly record struct QuestFlagRef(
    int Flag, string FlagName, QuestFlagRelation Relation, QuestFlagSourceKind SourceKind,
    int SourceNumber, int Map, int Room, string SourceName, int Value, QuestFlagLine Line,
    string Command);

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

    private const int MaxCastByLength = 90;

    // How far a chain of random draws is followed back: a table drawn by a line of a table
    // drawn by a line … The data nests one or two deep.
    private const int MaxDrawDepth = 4;

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

            LineBuilder builder = new(this, tb, new QuestScriptNames(_cache), QuestDeathSpells.For(_cache));
            List<QuestFlagRef> refs = new();
            Dictionary<int, List<QuestFlagLine>> linesByFlag = new();
            HashSet<int> granted = QuestCrawler.DiscoverGrantedFlags(
                tb.Values.SelectMany(static row => (row.Action ?? string.Empty).Split('\n')));

            HashSet<(int, QuestFlagRelation, QuestFlagSourceKind, int, int, int, int, int, int)> seen = new();
            foreach ((int number, TbRow row) in tb.OrderBy(static kv => kv.Key))
            {
                if (string.IsNullOrEmpty(row.Action)
                    || row.Action.IndexOf("ability", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                List<SourceRoot> roots = builder.RootsOf(number);
                string[] rawLines = row.Action.Split('\n');
                // A textblock that repeats a line word for word adds nothing: the first
                // copy that passes ends the scan.
                HashSet<string> seenText = new(StringComparer.Ordinal);
                for (int order = 0; order < rawLines.Length; order++)
                {
                    string text = rawLines[order].Trim();
                    List<(QuestFlagRelation Rel, int Flag, int Value)> abilities = ParseAbilities(text);
                    if (abilities.Count == 0 || !seenText.Add(text)) continue;

                    QuestFlagLine line = builder.Build(number, order, new HashSet<int>());
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
                                root.Number, root.Map, root.Room, ResolveSourceName(root, builder.Names), value, line,
                                CommandText(line.Triggers, root)));
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

            snapshot = new Snapshot(refs, linesByFlag, granted, builder.Names);
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

    // ----- One script line -----

    // Builds the QuestFlagLine for a textblock line during one Rebuild: its triggers, its roots
    // and the lines that draw its textblock at random.
    private sealed class LineBuilder
    {
        private readonly QuestFlagIndex _owner;
        private readonly Dictionary<int, TbRow> _tb;
        private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _deaths;
        private readonly Dictionary<int, List<SourceRoot>> _roots = new();
        // Textblock → the (textblock, line) pairs that name it in a `random` step.
        private readonly Dictionary<int, List<(int Block, int Order)>> _drawers = new();

        public QuestScriptNames Names { get; }

        public LineBuilder(
            QuestFlagIndex owner, Dictionary<int, TbRow> tb, QuestScriptNames names,
            IReadOnlyDictionary<int, IReadOnlyList<int>> deaths)
        {
            _owner = owner;
            _tb = tb;
            _deaths = deaths;
            Names = names;

            foreach ((int number, TbRow row) in tb.OrderBy(static kv => kv.Key))
            {
                if (string.IsNullOrEmpty(row.Action)
                    || row.Action.IndexOf("random", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string[] lines = row.Action.Split('\n');
                for (int order = 0; order < lines.Length; order++)
                {
                    foreach (string rawStep in lines[order].Split(':'))
                    {
                        QuestScriptStep step = QuestScriptStep.Parse(rawStep);
                        if (step.Verb != "random" || step.Int(0) is not int table || table <= 0) continue;
                        if (!_drawers.TryGetValue(table, out List<(int, int)>? list)) _drawers[table] = list = new();
                        if (!list.Contains((number, order))) list.Add((number, order));
                    }
                }
            }
        }

        public List<SourceRoot> RootsOf(int number)
        {
            if (!_roots.TryGetValue(number, out List<SourceRoot>? roots))
                _roots[number] = roots = ResolveRoots(number, _tb);
            return roots;
        }

        // drawnFor holds the tables already being read further down the chain of draws, so a
        // table that draws itself (or a chain that loops) is not followed round again.
        public QuestFlagLine Build(int number, int order, HashSet<int> drawnFor)
        {
            TbRow row = _tb[number];
            string[] rawLines = (row.Action ?? string.Empty).Split('\n');
            // A leading bare number is a band only in a textblock called as a random table.
            bool randomTable = (row.CalledFrom ?? string.Empty)
                .Contains("Textblock(rndm)", StringComparison.OrdinalIgnoreCase);
            QuestScriptLine script = QuestScriptLine.Parse(rawLines[order], randomTable);
            List<SourceRoot> roots = RootsOf(number);

            List<QuestFlagLine> callers = new();
            if (drawnFor.Count < MaxDrawDepth
                && _drawers.TryGetValue(number, out List<(int Block, int Order)>? drawers))
            {
                HashSet<int> inner = new(drawnFor) { number };
                HashSet<(int, string)> seenCaller = new();
                foreach ((int block, int callerOrder) in drawers)
                {
                    if (inner.Contains(block)) continue;
                    QuestFlagLine caller = Build(block, callerOrder, inner);
                    if (seenCaller.Add((block, caller.Script.Raw))) callers.Add(caller);
                }
            }

            return new QuestFlagLine(
                number, order, script, CleanText(row.CalledFrom),
                ResolveTriggers(number, script, row.CalledFrom, roots),
                RollChance(script, rawLines), _owner.Sources(roots, Names), script.LevelText,
                string.Join(", ", script.Classes.Select(Names.Class)),
                string.Join(", ", script.Races.Select(Names.Race)),
                ItemsText(script, Names), callers);
        }

        // A line that opens with a typed command belongs to the room (typed as written) or the
        // NPC (asked as a keyword) its textblock hangs off. A line that opens with a directive
        // is reached through an NPC's keyword — askable, or one the NPC shows by itself — or,
        // when its chain starts at a spell some monster's death casts, by killing that
        // monster. Anything else is left unresolved.
        private List<QuestTrigger> ResolveTriggers(
            int number, QuestScriptLine script, string? calledFrom, List<SourceRoot> roots)
        {
            List<QuestTrigger> triggers = new();
            if (script.Command is { } typed)
            {
                foreach (CalledFromRef r in ParseCalledFrom(calledFrom))
                {
                    if (r.Kind == CfKind.Room)
                        triggers.Add(new QuestTrigger(QuestTriggerKind.RoomCommand, typed, Map: r.Map, Room: r.Room));
                    else if (r.Kind == CfKind.Monster)
                        triggers.Add(new QuestTrigger(
                            QuestTriggerKind.Ask, AskCommand(r.Number, typed), r.Number, Textblock: number));
                }
                return triggers;
            }

            foreach ((int monster, int block, List<string> keywords) in NpcKeywords(number))
            {
                List<string> askable = keywords
                    .Where(k => !QuestStepGraph.IsAutoShownKeyword(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (askable.Count > 0)
                {
                    if (triggers.Exists(t => t.Kind == QuestTriggerKind.Ask && t.Monster == monster)) continue;
                    triggers.Add(new QuestTrigger(
                        QuestTriggerKind.Ask, AskCommand(monster, askable[0]), monster, Textblock: block)
                    {
                        OtherKeywords = askable.Skip(1).ToArray(),
                    });
                }
                else if (!triggers.Exists(t => t.Monster == monster))
                {
                    triggers.Add(new QuestTrigger(QuestTriggerKind.AutoShown, keywords[0], monster, Textblock: block)
                    {
                        OtherKeywords = keywords.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    });
                }
            }

            foreach (SourceRoot root in roots)
            {
                if (root.Kind != QuestFlagSourceKind.Spell
                    || !_deaths.TryGetValue(root.Number, out IReadOnlyList<int>? monsters))
                    continue;
                foreach (int monster in monsters)
                    triggers.Add(new QuestTrigger(
                        QuestTriggerKind.Kill, "kill " + Names.Monster(monster).ToLowerInvariant(),
                        monster, Spell: root.Number));
            }
            return triggers;
        }

        // Every NPC whose keyword block leads to the textblock, with the keywords that do. An
        // NPC's keyword block is the one whose Called From names the monster; it lists
        // `keyword:textblock` lines, so the keyword is looked up for the block walked in from —
        // the line's own textblock when it hangs straight off the keyword block. Shared
        // dialogue has several parents, and each is followed.
        private List<(int Monster, int Block, List<string> Keywords)> NpcKeywords(int start)
        {
            List<(int, int, List<string>)> found = new();
            HashSet<int> visited = new() { start };
            Queue<(int Block, int Child)> pending = new();
            foreach (CalledFromRef r in ParseCalledFrom(_tb[start].CalledFrom))
                if (r.Kind == CfKind.Textblock && visited.Add(r.Number)) pending.Enqueue((r.Number, start));

            int steps = 0;
            while (pending.Count > 0 && steps++ < MaxWalkSteps)
            {
                (int block, int child) = pending.Dequeue();
                if (!_tb.TryGetValue(block, out TbRow row)) continue;
                List<string>? keywords = null;
                foreach (CalledFromRef r in ParseCalledFrom(row.CalledFrom))
                {
                    if (r.Kind == CfKind.Monster)
                    {
                        keywords ??= QuestStepGraph.FindDispatchKeywords(row.Action, child);
                        if (keywords.Count > 0) found.Add((r.Number, block, keywords));
                    }
                    else if (r.Kind == CfKind.Textblock && visited.Add(r.Number))
                    {
                        pending.Enqueue((r.Number, block));
                    }
                }
            }
            return found;
        }

        // The ask target is the NPC's full name with a leading article dropped.
        private string AskCommand(int monster, string keyword)
            => $"ask {GuardDoorCommandResolver.AskTarget(Names.Monster(monster)).ToLowerInvariant()} {keyword}";
    }

    // The table's Command cell for one row: the ways to set the line off from that row's own
    // source. A keyword the NPC shows by itself is not a command, so it leaves the cell empty.
    private static string CommandText(IReadOnlyList<QuestTrigger> triggers, SourceRoot root)
    {
        List<string> parts = new();
        foreach (QuestTrigger t in triggers)
        {
            bool own = t.Kind switch
            {
                QuestTriggerKind.RoomCommand => root.Kind == QuestFlagSourceKind.Room && t.Map == root.Map && t.Room == root.Room,
                QuestTriggerKind.Ask         => root.Kind == QuestFlagSourceKind.Monster && t.Monster == root.Number,
                QuestTriggerKind.Kill        => root.Kind == QuestFlagSourceKind.Spell && t.Spell == root.Number,
                _                            => false,
            };
            if (!own) continue;
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

    private List<QuestFlagSource> Sources(List<SourceRoot> roots, QuestScriptNames names)
    {
        List<QuestFlagSource> sources = new();
        foreach (SourceRoot root in roots)
        {
            if (root.Kind == QuestFlagSourceKind.Textblock) continue;
            string name = ResolveSourceName(root, names);
            sources.Add(new QuestFlagSource(root.Kind, root.Number, root.Map, root.Room, root.Kind switch
            {
                QuestFlagSourceKind.Room    => $"room {name} ({root.Map}/{root.Room})",
                QuestFlagSourceKind.Monster => $"monster {name} (#{root.Number})",
                _                           => $"spell {name} (#{root.Number}{CastBy(root.Number)})",
            }));
        }
        return sources;
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
    private static List<SourceRoot> ResolveRoots(int startNumber, Dictionary<int, TbRow> tb)
    {
        List<SourceRoot> roots = new();
        if (!tb.TryGetValue(startNumber, out TbRow start)) return roots;

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
        return roots;
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
