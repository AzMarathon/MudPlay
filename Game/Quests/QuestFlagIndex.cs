using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

// How a TBInfo Action directive relates the block to a quest flag.
public enum QuestFlagRelation { Grants, Advances, Requires, Tests, Gate, Clears }

// The record kind a flag reference roots at, via the block's Called-From chain.
public enum QuestFlagSourceKind { Monster, Room, Spell, Textblock }

// One (flag, relationship, source) fact: a TBInfo directive touches a quest flag, attributed
// to the NPC / room / spell that reaches it. Value is the directive's second argument
// (give-step, required value, addability delta); 0 when the verb takes none (removeability)
// or it was absent.
public readonly record struct QuestFlagRef(
    int Flag, string FlagName, QuestFlagRelation Relation, QuestFlagSourceKind SourceKind,
    int SourceNumber, int Map, int Room, string SourceName, int Value);

// Lazy per-set index of every quest-flag reference in the active set's TBInfo table — the data
// behind the Game Data Browser's Quest Flags view. Mirrors ItemSourceIndex in shape: it scans
// each TBInfo Action for the ability directives (give/add/check/test/fail/removeability),
// attributes each to its monster / room / spell root by walking the block's Called-From
// provenance, and resolves ids to names via GameDataCache. Builds on first query and
// self-invalidates on a set swap by comparing the cache's ActiveSet.
public sealed class QuestFlagIndex
{
    private readonly GameDataCache _cache;
    private readonly List<QuestFlagRef> _refs = new();
    private string? _loadedSet;
    private bool _built;

    // Provenance-walk safety cap — a Called-From chain is a small acyclic tree in practice, but
    // the visited-set plus this bound keeps a malformed cycle from spinning.
    private const int MaxWalkSteps = 512;

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

    public QuestFlagIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // Every quest-flag reference in the active set, sorted flag → relationship → source. Live
    // view — read, don't mutate.
    public IReadOnlyList<QuestFlagRef> Entries
    {
        get { EnsureBuilt(); return _refs; }
    }

    private void EnsureBuilt()
    {
        string? active = _cache.ActiveSet;
        if (_built && _loadedSet == active) return;
        Rebuild(active);
    }

    private readonly record struct TbRow(string? Action, string? CalledFrom);

    private void Rebuild(string? active)
    {
        _refs.Clear();
        _loadedSet = active;
        _built = true;
        if (string.IsNullOrWhiteSpace(active)) return;

        JsonDocument? doc = _cache.GetRawTable("TBInfo");
        if (doc is null) return;

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

        Dictionary<(int, int), string> roomNames = BuildRoomNameMap();

        HashSet<(int, QuestFlagRelation, QuestFlagSourceKind, int, int, int, int)> seen = new();
        List<SourceRoot> roots = new();
        foreach ((int number, TbRow row) in tb)
        {
            if (string.IsNullOrEmpty(row.Action)
                || row.Action.IndexOf("ability", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            ResolveRoots(number, tb, roots);
            foreach ((QuestFlagRelation rel, int flag, int value) in ParseAbilities(row.Action))
            {
                foreach (SourceRoot root in roots)
                {
                    var key = (flag, rel, root.Kind, root.Number, root.Map, root.Room, value);
                    if (!seen.Add(key)) continue;
                    _refs.Add(new QuestFlagRef(
                        flag, AbilityNames.FormatId(flag), rel, root.Kind,
                        root.Number, root.Map, root.Room, ResolveSourceName(root, roomNames), value));
                }
            }
        }

        _refs.Sort(static (a, b) =>
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
            return c != 0 ? c : a.Room.CompareTo(b.Room);
        });
    }

    // Every ability directive in an Action string. Action is newline-separated, each line a
    // colon-delimited directive chain; a token whose head verb is one of the ability verbs
    // names a flag (first arg) and optional value (second arg).
    private static IEnumerable<(QuestFlagRelation Rel, int Flag, int Value)> ParseAbilities(string action)
    {
        foreach (string rawLine in action.Split('\n'))
        {
            foreach (string rawTok in rawLine.Split(':'))
            {
                string[] p = rawTok.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !s_verbs.TryGetValue(p[0], out QuestFlagRelation rel)) continue;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag)
                    || flag <= 0)
                    continue;
                int value = 0;
                if (p.Length >= 3)
                    int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                yield return (rel, flag, value);
            }
        }
    }

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

    private string ResolveSourceName(SourceRoot root, Dictionary<(int, int), string> roomNames) => root.Kind switch
    {
        QuestFlagSourceKind.Monster => Named("Monsters", root.Number, $"Monster #{root.Number}"),
        QuestFlagSourceKind.Spell   => Named("Spells", root.Number, $"Spell #{root.Number}"),
        QuestFlagSourceKind.Room    => roomNames.TryGetValue((root.Map, root.Room), out string? rn) && rn.Length > 0
                                        ? rn : $"Room {root.Map}/{root.Room}",
        _                           => $"Textblock #{root.Number}",
    };

    private string Named(string table, int number, string fallback)
        => _cache.FindNameByNumber(table, number) is { Length: > 0 } n ? n : fallback;

    private Dictionary<(int, int), string> BuildRoomNameMap()
    {
        Dictionary<(int, int), string> map = new();
        JsonDocument? doc = _cache.GetRawTable("Rooms");
        if (doc is null) return map;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(row, "Map Number", out int m) || !TryInt(row, "Room Number", out int r)) continue;
            if (row.TryGetProperty("Name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
                map[(m, r)] = nm.GetString() ?? string.Empty;
        }
        return map;
    }

    private static string? ReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool TryInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

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
