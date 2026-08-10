using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// One container that can yield an item — the reverse of ChestContentsReader:
// which chest an item drops from, and the per-open chance that a single `open`
// produces at least one of it.
public readonly record struct ItemSource(int ContainerItemId, string ContainerName, double Probability);

// Whether a textblock item-award roots at a monster (dialogue / turn-in) or a
// room (a room CMD like the dragon-statue "insert fang" → dragon key).
public enum ItemGiverKind { Monster, Room }

// One NPC or room that hands an item over through a TBInfo `giveitem` directive,
// attributed via the block's Called-From chain to its monster / room root. The
// requirement note ("turn in <item>", "purchase", "quest reward") is empty when
// the award line carries no gate.
//
// Keyword is the textblock trigger that reaches the give — the menu key on the
// root-adjacent block that leads down to the `giveitem` line (e.g. "orb" for the
// gnome commander's bloodstone orb, "touch statue" for a room CMD). Empty when
// the give fires on a bare greeting with no keyword to ask. Deterministic is
// true when the award is an unconditional hand-over — no turn-in / purchase /
// quest-reward gate and no `random` roll — so a walk can rely on issuing the
// command once and receiving the item. These two back the path-item keyword
// acquisition router: a Monster giver is asked `ask <Name> <Keyword>`, a Room
// giver is sent `<Keyword>` verbatim.
public readonly record struct ItemGiver(
    ItemGiverKind Kind, int Number, int Map, int Room, string Name, string Requirement,
    string Keyword, bool Deterministic);

// Lazy per-set reverse index of the two item-acquisition paths the shop/drop
// indexes don't cover: containers (an item's chest sources — the inverse of
// ChestContentsReader) and textblock `giveitem` awards (quest turn-ins, room
// CMD rewards, and merchant gives, attributed to their monster / room via each
// TBInfo entry's Called-From chain). Backs the Game Data Browser's item detail
// pane only, so unlike the eager routing indexes (ShopStockIndex /
// MonsterDropIndex) it builds on first query and self-invalidates by comparing
// the cache's ActiveSet to the set it last built from — no ActiveSetChanged
// subscription, and it never evicts tables (the browser is actively reading
// them). `roomitem` awards are deliberately excluded: that verb scatter-places
// an item in a room rather than handing it to the player on a gated exchange.
public sealed class ItemSourceIndex
{
    private readonly GameDataCache _cache;
    private readonly TBInfoStore _tb;
    private readonly LogService? _log;

    private readonly Dictionary<int, List<ItemSource>> _containersByItem = new();
    private readonly Dictionary<int, List<ItemGiver>> _giversByItem = new();
    private readonly Dictionary<int, List<RoomKey>> _giverRoomsByMonster = new();

    // Set the maps were last built from; compared against _cache.ActiveSet to
    // self-invalidate on a set swap. Null both means "never built".
    private string? _loadedSet;
    private bool _built;

    // Provenance-walk safety caps: a Called-From chain is a small acyclic tree in
    // practice, but the visited-set plus these bounds keep a malformed cycle or a
    // pathologically deep chain from spinning.
    private const int MaxWalkSteps = 512;

    public ItemSourceIndex(GameDataCache cache, TBInfoStore tb, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tb);
        _cache = cache;
        _tb = tb;
        _log = log;
    }

    // Containers that can yield itemId, highest drop chance first, or an empty
    // list when nothing in the active set drops it. Live view — read, don't mutate.
    public IReadOnlyList<ItemSource> ContainersOf(int itemId)
    {
        EnsureBuilt();
        return _containersByItem.TryGetValue(itemId, out List<ItemSource>? list)
            ? list
            : Array.Empty<ItemSource>();
    }

    // Monsters / rooms that hand itemId over via a textblock `giveitem`, or an
    // empty list when nothing gives it. Live view — read, don't mutate.
    public IReadOnlyList<ItemGiver> GiversOf(int itemId)
    {
        EnsureBuilt();
        return _giversByItem.TryGetValue(itemId, out List<ItemGiver>? list)
            ? list
            : Array.Empty<ItemGiver>();
    }

    // Rooms where the giver monster with monsterId spawns (from Monsters.json
    // "Summoned By"), or empty when it names none. Resolves a Monster giver's
    // location for the acquisition router — an ItemGiver carries Map/Room=0 for
    // the Monster kind because the give is attributed by dialogue provenance, not
    // placement. Live view — read, don't mutate.
    public IReadOnlyList<RoomKey> GiverMonsterRoomsOf(int monsterId)
    {
        EnsureBuilt();
        return _giverRoomsByMonster.TryGetValue(monsterId, out List<RoomKey>? rooms)
            ? rooms
            : Array.Empty<RoomKey>();
    }

    private void EnsureBuilt()
    {
        string? active = _cache.ActiveSet;
        if (_built && _loadedSet == active) return;
        Rebuild(active);
    }

    private void Rebuild(string? active)
    {
        _containersByItem.Clear();
        _giversByItem.Clear();
        _giverRoomsByMonster.Clear();
        _loadedSet = active;
        _built = true;

        if (string.IsNullOrWhiteSpace(active))
        {
            _log?.Info("ItemSourceIndex", "No active set; cleared.");
            return;
        }

        BuildContainers();
        BuildGivers();
        BuildGiverMonsterRooms();

        _log?.Info("ItemSourceIndex",
            $"Indexed {_containersByItem.Count} container-sourced item(s) and " +
            $"{_giversByItem.Count} textblock-given item(s) from '{active}'.");
    }

    // Invert ChestContentsReader.ReadAll (container → drops) into item → containers.
    private void BuildContainers()
    {
        IReadOnlyDictionary<int, ChestContents> chests = ChestContentsReader.ReadAll(_cache);
        foreach ((int containerId, ChestContents contents) in chests)
        {
            string containerName = _cache.FindNameByNumber("Items", containerId) is { Length: > 0 } n
                ? n
                : $"#{containerId.ToString(CultureInfo.InvariantCulture)}";
            foreach (ChestDrop drop in contents.Drops)
            {
                if (!_containersByItem.TryGetValue(drop.ItemId, out List<ItemSource>? list))
                    _containersByItem[drop.ItemId] = list = new List<ItemSource>();
                list.Add(new ItemSource(containerId, containerName, drop.Probability));
            }
        }
        // Highest-chance source first so the detail pane leads with the most
        // likely chest to open.
        foreach (List<ItemSource> list in _containersByItem.Values)
            list.Sort(static (a, b) => b.Probability.CompareTo(a.Probability));
    }

    // Walk every TBInfo entry that hands out an item, attribute each award to its
    // monster / room root, and record the per-award requirement gate.
    private void BuildGivers()
    {
        Dictionary<int, string> itemNames = BuildNameMap("Items");
        Dictionary<int, string> monsterNames = BuildNameMap("Monsters");
        Dictionary<(int Map, int Room), string> roomNames = BuildRoomNameMap();

        var roots = new List<GiverRoot>();
        var giveItems = new List<int>();

        foreach (TBInfoEntry entry in _tb.Entries)
        {
            string? action = entry.Action;
            if (string.IsNullOrEmpty(action)
                || action.IndexOf("giveitem", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            roots.Clear();
            ResolveRoots(entry, roots);
            if (roots.Count == 0) continue;   // orphaned block — nothing to attribute to.

            foreach (string rawLine in action.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                giveItems.Clear();
                int takeId = 0;
                bool hasPrice = false, hasAbility = false, hasRandom = false;

                // A single-block give carries its trigger as the leading token of
                // the same line ("give blade:takeitem 20:giveitem 22"); a
                // multi-block give leads with the directive ("giveitem 807:...")
                // and takes its keyword from the parent menu (root.Keyword). The
                // leading token is a keyword only when it isn't itself a directive.
                string[] toks = line.Split(':');
                string lineKeyword = toks.Length > 0 && !LooksLikeDirective(toks[0].Trim())
                    ? toks[0].Trim()
                    : string.Empty;

                foreach (string rawTok in toks)
                {
                    string tok = rawTok.Trim();
                    if (TryArg(tok, "giveitem", out int gi) && gi > 0) giveItems.Add(gi);
                    else if (takeId == 0 && TryArg(tok, "takeitem", out int ti) && ti > 0) takeId = ti;
                    else if (tok.StartsWith("price", StringComparison.OrdinalIgnoreCase)) hasPrice = true;
                    else if (tok.StartsWith("giveability", StringComparison.OrdinalIgnoreCase)) hasAbility = true;
                    else if (tok.StartsWith("random", StringComparison.OrdinalIgnoreCase)) hasRandom = true;
                }
                if (giveItems.Count == 0) continue;

                // Turn-in names the required item (the most useful hint); a bare
                // price is a purchase; a lone giveability marks a quest reward.
                string requirement =
                    takeId > 0 ? "turn in " + ResolveName(itemNames, takeId, $"item #{takeId}")
                    : hasPrice ? "purchase"
                    : hasAbility ? "quest reward"
                    : string.Empty;

                // Deterministic only when the hand-over is unconditional — no
                // turn-in / purchase / quest-reward gate and no `random` roll on
                // the award line — so the acquisition router can rely on the
                // command producing the item every time.
                bool deterministic = takeId == 0 && !hasPrice && !hasAbility && !hasRandom;

                foreach (int itemId in giveItems)
                {
                    foreach (GiverRoot root in roots)
                    {
                        string name = root.Kind == ItemGiverKind.Monster
                            ? ResolveName(monsterNames, root.Number, $"Monster #{root.Number}")
                            : (roomNames.TryGetValue((root.Map, root.Room), out string? rn) && rn.Length > 0
                                ? rn
                                : $"Room {root.Map}/{root.Room}");
                        // A single-block give supplies its own trigger on the award
                        // line; only fall back to the parent-menu keyword when this
                        // line leads with a directive.
                        string keyword = lineKeyword.Length > 0 ? lineKeyword : root.Keyword;
                        AddGiver(itemId,
                            new ItemGiver(root.Kind, root.Number, root.Map, root.Room, name, requirement,
                                keyword, deterministic));
                    }
                }
            }
        }
    }

    // Resolve each giver monster's spawn rooms off Monsters.json "Summoned By".
    // Only the monsters that actually give an item are looked up — parsing every
    // monster's (often hundreds-long) Summoned By would be wasted work, and the
    // router only ever asks where a known giver lives.
    private void BuildGiverMonsterRooms()
    {
        var giverMonsters = new HashSet<int>();
        foreach (List<ItemGiver> givers in _giversByItem.Values)
            foreach (ItemGiver g in givers)
                if (g.Kind == ItemGiverKind.Monster && g.Number > 0)
                    giverMonsters.Add(g.Number);
        if (giverMonsters.Count == 0) return;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(row, "Number", out int num) || !giverMonsters.Contains(num)) continue;
            if (!row.TryGetProperty("Summoned By", out JsonElement s)
                || s.ValueKind != JsonValueKind.String)
                continue;
            if (MonsterDropIndex.ParseSummonedByRooms(s.GetString()) is { } rooms)
                _giverRoomsByMonster[num] = rooms;
        }
    }

    // Walk the entry's Called-From graph upward, collecting the distinct monster /
    // room roots and the keyword that reaches the give from each. Textblock refs
    // recurse (a block called by another block); spell refs stop the walk — an
    // item cast out of a spell is either chest loot (the ContainersOf path already
    // covers it) or a self-cast, neither of which is a walkable "go here to get
    // it" source.
    //
    // The keyword is the menu key on the root-adjacent block whose value is the
    // child block on the path down toward the giveitem: the greeting menu
    // "orb:814" links the gnome commander to the bloodstone-orb sub-chain, so an
    // orb award attributed to that monster carries keyword "orb". Each queued
    // step tracks the child (the block we arrived from) so a root ref can read its
    // trigger off the current block's menu. A give reached only through LinkTo
    // continuations (no menu key) yields an empty keyword — a bare-greeting give.
    private void ResolveRoots(TBInfoEntry entry, List<GiverRoot> roots)
    {
        var seenTb = new HashSet<int> { entry.Number };
        var pending = new Queue<(TBInfoEntry Block, int Child)>();
        pending.Enqueue((entry, 0));

        int steps = 0;
        while (pending.Count > 0 && steps++ < MaxWalkSteps)
        {
            (TBInfoEntry block, int child) = pending.Dequeue();
            foreach (CalledFromRef r in ParseCalledFrom(block.CalledFrom))
            {
                switch (r.Kind)
                {
                    case CfKind.Monster:
                        AddRoot(roots, new GiverRoot(ItemGiverKind.Monster, r.Number, 0, 0,
                            ExtractKeyword(block.Action, child)));
                        break;
                    case CfKind.Room:
                        AddRoot(roots, new GiverRoot(ItemGiverKind.Room, 0, r.Map, r.Room,
                            ExtractKeyword(block.Action, child)));
                        break;
                    case CfKind.Textblock:
                        if (r.Number > 0 && seenTb.Add(r.Number)
                            && _tb.GetEntry(r.Number) is { } parent)
                            pending.Enqueue((parent, block.Number));
                        break;
                    case CfKind.Spell:
                        break;   // dead-ends the walk (see method note).
                }
            }
        }
    }

    // The menu key on a textblock whose target block is `child`. A menu block's
    // Action is newline-separated "keyword:blockNumber" lines (the greeting
    // routes each keyword to a sub-block); return the keyword whose target is the
    // block we descended into. Empty when `child` is unset or reached via a
    // LinkTo continuation rather than a keyed menu entry.
    private static string ExtractKeyword(string? action, int child)
    {
        if (child <= 0 || string.IsNullOrEmpty(action)) return string.Empty;
        foreach (string rawLine in action.Split('\n'))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;
            if (LeadingInt(rawLine[(colon + 1)..]) != child) continue;
            string kw = rawLine[..colon].Trim();
            // A "checkitem 20:815"-style directive line points at a child block
            // too but isn't a menu key — skip it so only real keywords surface.
            if (kw.Length > 0 && !LooksLikeDirective(kw)) return kw;
        }
        return string.Empty;
    }

    private void AddGiver(int itemId, ItemGiver giver)
    {
        if (!_giversByItem.TryGetValue(itemId, out List<ItemGiver>? list))
            _giversByItem[itemId] = list = new List<ItemGiver>();

        // Dedup by source identity — the same monster / room reached through
        // several award lines is one row. Keep the first requirement / keyword
        // seen, but backfill each if the earlier hit had none, and treat the row
        // as deterministic if any award line to it is (a reliable hand-over
        // exists even if another line is gated).
        for (int i = 0; i < list.Count; i++)
        {
            ItemGiver e = list[i];
            if (e.Kind == giver.Kind && e.Number == giver.Number
                && e.Map == giver.Map && e.Room == giver.Room)
            {
                if (e.Requirement.Length == 0 && giver.Requirement.Length > 0)
                    e = e with { Requirement = giver.Requirement };
                if (e.Keyword.Length == 0 && giver.Keyword.Length > 0)
                    e = e with { Keyword = giver.Keyword };
                if (!e.Deterministic && giver.Deterministic)
                    e = e with { Deterministic = true };
                list[i] = e;
                return;
            }
        }
        list.Add(giver);
    }

    private static void AddRoot(List<GiverRoot> roots, GiverRoot root)
    {
        if (!roots.Contains(root)) roots.Add(root);
    }

    // ----- Called-From parsing -----

    private enum CfKind { Monster, Room, Textblock, Spell }
    private readonly record struct CalledFromRef(CfKind Kind, int Number, int Map, int Room);
    private readonly record struct GiverRoot(
        ItemGiverKind Kind, int Number, int Map, int Room, string Keyword);

    // A Called-From string is a comma-separated list of provenance tokens:
    // "Monster #246", "Room 7/1008", "Textblock #349", "Textblock(rndm) #868",
    // "Spell #559". Unknown / malformed tokens are skipped.
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
                // Covers both "Textblock #N" and "Textblock(rndm) #N".
                int n = IntAfterHash(part);
                if (n > 0) yield return new CalledFromRef(CfKind.Textblock, n, 0, 0);
            }
            else if (part.StartsWith("Spell", StringComparison.OrdinalIgnoreCase))
            {
                yield return new CalledFromRef(CfKind.Spell, 0, 0, 0);
            }
        }
    }

    // Leading integer after the first '#', or 0.
    private static int IntAfterHash(string s)
    {
        int hash = s.IndexOf('#');
        if (hash < 0) return 0;
        return LeadingInt(s[(hash + 1)..]);
    }

    // Parse the "map/room" pair out of "Room 7/1008".
    private static bool TryRoom(string s, out int map, out int room)
    {
        map = 0; room = 0;
        int slash = s.IndexOf('/');
        if (slash < 0) return false;
        // Map number is the trailing int of the segment before '/'.
        map = TrailingInt(s[..slash]);
        room = LeadingInt(s[(slash + 1)..]);
        return map > 0 && room > 0;
    }

    private Dictionary<int, string> BuildNameMap(string table)
    {
        var map = new Dictionary<int, string>();
        JsonDocument? doc = _cache.GetRawTable(table);
        if (doc is null) return map;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("Number", out JsonElement n) || n.ValueKind != JsonValueKind.Number) continue;
            if (!n.TryGetInt32(out int num) || num <= 0) continue;
            if (row.TryGetProperty("Name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
                map[num] = nm.GetString() ?? string.Empty;
        }
        return map;
    }

    private Dictionary<(int Map, int Room), string> BuildRoomNameMap()
    {
        var map = new Dictionary<(int, int), string>();
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

    private static string ResolveName(Dictionary<int, string> names, int number, string fallback)
        => names.TryGetValue(number, out string? n) && n.Length > 0 ? n : fallback;

    // ----- Directive-token parsing (giveitem / takeitem grammar) -----
    //
    // A trimmed, deliberately small duplicate of ChestContentsReader's private
    // token helpers: that copy parses the chest loot subset (giveitem / random),
    // this one parses the award subset (giveitem / takeitem) — same shape, but
    // the two grammars live with the code that owns them rather than sharing an
    // exported parser.

    private static bool TryArg(string tok, string keyword, out int value)
    {
        value = 0;
        if (!tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        int i = keyword.Length;
        if (i >= tok.Length || tok[i] != ' ') return false;
        value = LeadingInt(tok[(i + 1)..]);
        return true;
    }

    // The reserved TBInfo directive verbs. Used to tell a give's own trigger
    // (the leading token of a single-block "give blade:...:giveitem 22") from a
    // directive-led award line ("giveitem 807:..."): the former's first token is
    // a player keyword, the latter's is one of these.
    private static readonly HashSet<string> s_directiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "giveitem", "takeitem", "summon", "checkitem", "checkability", "giveability",
        "roomitem", "nomonsters", "price", "message", "text", "random",
        "minlevel", "maxlevel", "class", "failitem", "teleport", "cast",
    };

    // True when the token's first word is a reserved directive verb — i.e. the
    // token is a directive, not a player-typed keyword.
    private static bool LooksLikeDirective(string tok)
    {
        if (tok.Length == 0) return false;
        int space = tok.IndexOf(' ');
        string head = space < 0 ? tok : tok[..space];
        return s_directiveVerbs.Contains(head);
    }

    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start
            && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static int TrailingInt(string s)
    {
        int i = s.Length;
        while (i > 0 && s[i - 1] == ' ') i--;
        int end = i;
        while (i > 0 && s[i - 1] is >= '0' and <= '9') i--;
        return end > i
            && int.TryParse(s.AsSpan(i, end - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static bool TryInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
