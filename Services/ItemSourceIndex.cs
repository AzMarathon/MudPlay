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
//
// One row per source AND keyword: the old hermit hands the jagged bone key over on
// `gift` (a quest turn-in) and again on `remind` (free, to anyone past that step),
// and folding the two together asked him for the wrong one.
//
// RefusalMessages are the message-table numbers the award line prints when one of
// its conditions fails ("goodaligned -51 3075" → message 3075). A give carrying a
// condition can be refused; these are how the refusal reads on the wire.
//
// TradeItemId is the one item a plain exchange takes: an award line that does
// nothing but take a single item and hand its award over ("takeitem 811
// 1368:giveitem 808:text 843", the sleazy shopkeeper's glowing key for an opal
// brooch), and is the only line its keyword runs. Such a give isn't Deterministic,
// since it costs the item, but it is as certain as a free one for a character
// holding that item. Zero for every other give: a line that takes more than one
// item, takes one twice, carries any other condition, or shares its keyword with
// another line (which of several lines the game runs isn't something to assume).
public readonly record struct ItemGiver(
    ItemGiverKind Kind, int Number, int Map, int Room, string Name, string Requirement,
    string Keyword, bool Deterministic, IReadOnlyList<int>? RefusalMessages = null,
    int TradeItemId = 0);

// One monster a room command summons on demand, which drops an item at a
// guaranteed rate. This is the deterministic corner of monster drops, and the
// reason it's indexed separately from MonsterDropIndex: the spawn is not lair RNG
// (the room command conjures it whenever asked) and the drop is not a percentage
// roll, so a walk can rely on the entire chain. That's what separates the town
// gate key — `touch statue` in 8/461 summons the obsidian statue, which drops it
// 100% of the time — from the black star key, whose dropper only appears on a lair
// roll at a low rate and can never be relied on mid-route.
//
// Command is the room-CMD keyword to type, Map/Room the room to type it in.
// DropPercent is always 100 by construction; it's carried so the log and the route
// picker can state the guarantee rather than implying one.
public readonly record struct SummonDropSource(
    int MonsterId, string MonsterName, int Map, int Room, string Command, int DropPercent);

// Lazy per-set reverse index of the item-acquisition paths the shop/drop indexes
// don't cover: containers (an item's chest sources — the inverse of
// ChestContentsReader), textblock `giveitem` awards (quest turn-ins, room CMD
// rewards, and merchant gives, attributed to their monster / room via each TBInfo
// entry's Called-From chain), and room-command summons whose monster drops an item
// outright. Backs both the Game Data Browser's item detail pane and the
// deterministic acquisition routers (PathItemGiveRouter / PathItemSummonRouter),
// so unlike the eager indexes (ShopStockIndex / MonsterDropIndex) it builds on
// first query and self-invalidates by comparing the cache's ActiveSet to the set
// it last built from — no ActiveSetChanged subscription, and it never evicts
// tables (the browser is actively reading them). `roomitem` awards are
// deliberately excluded from the giver map: that verb scatter-places an item in a
// room rather than handing it to the player on a gated exchange.
public sealed class ItemSourceIndex
{
    private readonly GameDataCache _cache;
    private readonly TBInfoStore _tb;
    private readonly LogService? _log;

    // One immutable build, published by a single reference assignment. Readers take
    // the current snapshot and work from it, so a rebuild can run on a background
    // thread without ever handing a caller a half-cleared dictionary — which is what
    // lets the warm-up below happen off the UI thread.
    private sealed class Snapshot
    {
        public required string? Set { get; init; }
        public required Dictionary<int, List<ItemSource>> Containers { get; init; }
        public required Dictionary<int, List<ItemGiver>> Givers { get; init; }
        public required Dictionary<int, List<RoomKey>> GiverRooms { get; init; }
        public required Dictionary<int, List<SummonDropSource>> SummonDrops { get; init; }
        public required int RoomCommandItems { get; init; }
    }

    // Serialises builders so two threads racing the same set build once, not twice.
    private readonly object _buildGate = new();
    private volatile Snapshot? _snapshot;

    // A drop only counts as a guarantee at the top of the range. Anything less is
    // a roll, which is MonsterDropRouter's prompt-first territory.
    private const int GuaranteedDropPercent = 100;

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
        return EnsureBuilt().Containers.TryGetValue(itemId, out List<ItemSource>? list)
            ? list
            : Array.Empty<ItemSource>();
    }

    // Monsters / rooms that hand itemId over via a textblock `giveitem`, or an
    // empty list when nothing gives it. Live view — read, don't mutate.
    public IReadOnlyList<ItemGiver> GiversOf(int itemId)
    {
        return EnsureBuilt().Givers.TryGetValue(itemId, out List<ItemGiver>? list)
            ? list
            : Array.Empty<ItemGiver>();
    }

    // How many items in the active set list a "Room(<command>) <rooms>" source in
    // their Obtained From. Zero for an export that doesn't write them, which is most.
    public int RoomCommandItemCount => EnsureBuilt().RoomCommandItems;

    // Rooms where the giver monster with monsterId spawns (from Monsters.json
    // "Summoned By"), or empty when it names none. Resolves a Monster giver's
    // location for the acquisition router — an ItemGiver carries Map/Room=0 for
    // the Monster kind because the give is attributed by dialogue provenance, not
    // placement. Live view — read, don't mutate.
    public IReadOnlyList<RoomKey> GiverMonsterRoomsOf(int monsterId)
    {
        return EnsureBuilt().GiverRooms.TryGetValue(monsterId, out List<RoomKey>? rooms)
            ? rooms
            : Array.Empty<RoomKey>();
    }

    // Room commands that summon a monster which then drops itemId outright, or an
    // empty list when no such command exists. Live view — read, don't mutate.
    public IReadOnlyList<SummonDropSource> SummonDropsOf(int itemId)
    {
        return EnsureBuilt().SummonDrops.TryGetValue(itemId, out List<SummonDropSource>? list)
            ? list
            : Array.Empty<SummonDropSource>();
    }

    // Build the index for the active set ahead of anyone asking for it. Safe to
    // call from a worker thread — the build touches only locals until it publishes,
    // and GameDataCache guards its own tables — so the ~600 ms first build lands
    // while the set loads instead of inside the first walk that crosses a gate.
    // A query racing the warm just builds it itself; the gate makes one of them wait
    // rather than doing the work twice.
    public void Warm()
    {
        try
        {
            EnsureBuilt();
        }
        catch (Exception ex)
        {
            // A warm-up is an optimisation; a failure here must not take down the
            // thread it runs on. The next query rebuilds and surfaces the fault.
            _log?.Warn("ItemSourceIndex", $"Warm-up failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private Snapshot EnsureBuilt()
    {
        string? active = _cache.ActiveSet;
        if (_snapshot is { } current && current.Set == active) return current;

        lock (_buildGate)
        {
            // Another thread may have published while we waited for the gate.
            if (_snapshot is { } published && published.Set == active) return published;
            Snapshot built = Build(active);
            _snapshot = built;
            return built;
        }
    }

    private Snapshot Build(string? active)
    {
        var containers = new Dictionary<int, List<ItemSource>>();
        var givers = new Dictionary<int, List<ItemGiver>>();
        var giverRooms = new Dictionary<int, List<RoomKey>>();
        var summonDrops = new Dictionary<int, List<SummonDropSource>>();
        int roomCommandItems = 0;

        if (string.IsNullOrWhiteSpace(active))
        {
            _log?.Info("ItemSourceIndex", "No active set; cleared.");
        }
        else
        {
            BuildContainers(containers);
            BuildGivers(givers);
            roomCommandItems = CompleteRoomGiversFromItemSources(givers);
            BuildGiverMonsterRooms(givers, giverRooms);
            BuildSummonDrops(summonDrops);

            _log?.Info("ItemSourceIndex",
                $"Indexed {containers.Count} container-sourced item(s), " +
                $"{givers.Count} textblock-given item(s) and " +
                $"{summonDrops.Count} summon-dropped item(s) from '{active}'" +
                (roomCommandItems > 0
                    ? $"; {roomCommandItems} item(s) list room-command sources."
                    : "; no room-command sources in this export."));
        }

        return new Snapshot
        {
            Set = active,
            Containers = containers,
            Givers = givers,
            GiverRooms = giverRooms,
            SummonDrops = summonDrops,
            RoomCommandItems = roomCommandItems,
        };
    }

    // Invert ChestContentsReader.ReadAll (container → drops) into item → containers.
    private void BuildContainers(Dictionary<int, List<ItemSource>> containersByItem)
    {
        IReadOnlyDictionary<int, ChestContents> chests = ChestContentsReader.ReadAll(_cache);
        foreach ((int containerId, ChestContents contents) in chests)
        {
            string containerName = _cache.FindNameByNumber("Items", containerId) is { Length: > 0 } n
                ? n
                : $"#{containerId.ToString(CultureInfo.InvariantCulture)}";
            foreach (ChestDrop drop in contents.Drops)
            {
                if (!containersByItem.TryGetValue(drop.ItemId, out List<ItemSource>? list))
                    containersByItem[drop.ItemId] = list = new List<ItemSource>();
                list.Add(new ItemSource(containerId, containerName, drop.Probability));
            }
        }
        // Highest-chance source first so the detail pane leads with the most
        // likely chest to open.
        foreach (List<ItemSource> list in containersByItem.Values)
            list.Sort(static (a, b) => b.Probability.CompareTo(a.Probability));
    }

    // Walk every TBInfo entry that hands out an item, attribute each award to its
    // monster / room root, and record the per-award requirement gate.
    private void BuildGivers(Dictionary<int, List<ItemGiver>> giversByItem)
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

            string[] lines = action.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                giveItems.Clear();
                List<int>? refusals = null;
                List<int>? checkedItems = null;
                int takeId = 0, takes = 0;
                bool hasPrice = false, hasAbility = false, hasRandom = false;
                bool plainExchange = true;

                // A single-block give carries its trigger as the leading token of
                // the same line ("give blade:takeitem 20:giveitem 22"); a
                // multi-block give leads with the directive ("giveitem 807:...")
                // and takes its keyword from the parent menu (root.Keyword). The
                // leading token is a keyword only when it isn't itself a directive.
                string[] toks = line.Split(':');
                string lineKeyword = toks.Length > 0 && !LooksLikeDirective(toks[0].Trim())
                    ? toks[0].Trim()
                    : string.Empty;

                for (int t = 0; t < toks.Length; t++)
                {
                    string tok = toks[t].Trim();
                    if (TryArg(tok, "giveitem", out int gi) && gi > 0) giveItems.Add(gi);
                    else if (TryArg(tok, "takeitem", out int ti) && ti > 0)
                    {
                        takes++;
                        if (takeId == 0) takeId = ti;
                    }
                    else if (TryArg(tok, "checkitem", out int ci)) (checkedItems ??= new List<int>()).Add(ci);
                    else if (tok.StartsWith("price", StringComparison.OrdinalIgnoreCase)) hasPrice = true;
                    else if (tok.StartsWith("giveability", StringComparison.OrdinalIgnoreCase)) hasAbility = true;
                    else if (tok.StartsWith("random", StringComparison.OrdinalIgnoreCase)) hasRandom = true;
                    if (RefusalMessageOf(tok) is { } refusal) (refusals ??= new List<int>()).Add(refusal);
                    bool isLineKeyword = t == 0 && lineKeyword.Length > 0;
                    if (tok.Length > 0 && !isLineKeyword && !IsExchangeToken(tok)) plainExchange = false;
                }
                if (giveItems.Count == 0) continue;
                // A check for some other item is a condition, not part of the trade.
                if (checkedItems is not null && checkedItems.Exists(id => id != takeId)) plainExchange = false;

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

                // One item taken, once, nothing else asked of the character, and no
                // other line under the same keyword. A line that takes several, or
                // the same one more than once, is left out, and so is one of several
                // alternatives: how the game counts the first and picks among the
                // second isn't something to assume here.
                int tradeItemId = takes == 1 && plainExchange && LinesSharingTrigger(lines, lineKeyword) == 1
                    ? takeId
                    : 0;

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
                        AddGiver(giversByItem, itemId,
                            new ItemGiver(root.Kind, root.Number, root.Map, root.Room, name, requirement,
                                keyword, deterministic, refusals, tradeItemId));
                    }
                }
            }
        }
    }

    // Fill in the rooms a room-command give works in, from the items' own
    // "Room(<command>) <rooms>" sources, and count the items that carry any.
    //
    // The textblock walk above finds a room giver through the block's Called-From,
    // which the export cuts at 250 characters: the coffin commands are called from
    // 94 rooms and the cell names 18. The item's source lists them all. Only a
    // direct give is completed (a rolled one is no hand-over to route to), and only
    // where the walk already found that command giving the item in some room: that
    // row carries what the command asks for, which the source entry doesn't say.
    private int CompleteRoomGiversFromItemSources(Dictionary<int, List<ItemGiver>> giversByItem)
    {
        JsonDocument? items = _cache.GetRawTable("Items");
        if (items is null) return 0;

        Dictionary<(int Map, int Room), string>? roomNames = null;
        int withSources = 0;
        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !TryInt(row, "Number", out int itemId)) continue;
            if (!row.TryGetProperty("Obtained From", out JsonElement cell) || cell.ValueKind != JsonValueKind.String)
                continue;
            string? text = cell.GetString();
            if (text is null || !text.Contains("Room(", StringComparison.Ordinal)) continue;

            List<Game.GameData.RoomCommandSource> sources = Game.GameData.RoomCommandSourceParser.ParseCell(text);
            if (sources.Count == 0) continue;
            withSources++;
            if (!giversByItem.TryGetValue(itemId, out List<ItemGiver>? givers)) continue;

            foreach (Game.GameData.RoomCommandSource source in sources)
            {
                if (!source.IsDirect) continue;
                foreach (string command in source.Commands)
                {
                    int known = givers.FindIndex(g => g.Kind == ItemGiverKind.Room
                        && string.Equals(g.Keyword, command, StringComparison.OrdinalIgnoreCase));
                    if (known < 0) continue;
                    ItemGiver template = givers[known];
                    foreach ((int map, int room) in source.Rooms)
                    {
                        if (givers.Exists(g => g.Kind == ItemGiverKind.Room && g.Map == map && g.Room == room
                                && string.Equals(g.Keyword, command, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        roomNames ??= BuildRoomNameMap();
                        string name = roomNames.TryGetValue((map, room), out string? rn) && rn.Length > 0
                            ? rn
                            : $"Room {map}/{room}";
                        givers.Add(template with { Map = map, Room = room, Name = name });
                    }
                }
            }
        }
        return withSources;
    }

    // Index the room commands that summon a guaranteed dropper. Driven from the
    // ROOM side rather than the monster side: a monster's "Summoned By" only names
    // the textblock that conjures it ("Textblock #863"), so resolving it back to a
    // room would mean walking the Called-From chain, while every room CMD already
    // states its own summons directly. Walking rooms also keeps the result honest —
    // a summon reachable only from a textblock no room invokes isn't something a
    // walk could ever trigger.
    private void BuildSummonDrops(Dictionary<int, List<SummonDropSource>> summonDropsByItem)
    {
        Dictionary<int, (string Name, List<int> Drops)> guaranteed = BuildGuaranteedDroppers();
        if (guaranteed.Count == 0) return;

        JsonDocument? rooms = _cache.GetRawTable("Rooms");
        if (rooms is null) return;

        foreach (JsonElement row in rooms.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(row, "CMD", out int cmd) || cmd <= 0) continue;
            if (!TryInt(row, "Map Number", out int map) || !TryInt(row, "Room Number", out int room))
                continue;

            foreach (TBInfoActionResolver.RoomEffectCommand ec
                     in TBInfoActionResolver.EnumerateEffectCommands(_tb, cmd))
            {
                if (ec.Kind != TBInfoActionResolver.RoomEffectKind.Summon) continue;
                if (!guaranteed.TryGetValue(ec.TargetId, out (string Name, List<int> Drops) m)) continue;

                foreach (int itemId in m.Drops)
                {
                    if (!summonDropsByItem.TryGetValue(itemId, out List<SummonDropSource>? list))
                        summonDropsByItem[itemId] = list = new List<SummonDropSource>();
                    var src = new SummonDropSource(
                        ec.TargetId, m.Name, map, room, ec.Keyword, GuaranteedDropPercent);
                    // Synonyms ("touch statue" / "move statue") summon the same
                    // monster in the same room; one source is enough to route on.
                    if (!list.Any(s => s.MonsterId == src.MonsterId
                                       && s.Map == src.Map && s.Room == src.Room))
                        list.Add(src);
                }
            }
        }
    }

    // Monsters with at least one guaranteed drop, by id → (name, dropped item ids).
    // Only the guaranteed slots are collected; a monster's percentage drops stay
    // MonsterDropIndex's business.
    private Dictionary<int, (string Name, List<int> Drops)> BuildGuaranteedDroppers()
    {
        var byMonster = new Dictionary<int, (string, List<int>)>();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return byMonster;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(row, "Number", out int id) || id <= 0) continue;

            List<int>? drops = null;
            for (int slot = 0; slot < 10; slot++)
            {
                if (!TryInt(row, $"DropItem-{slot}", out int itemId) || itemId <= 0) continue;
                if (!TryInt(row, $"DropItem%-{slot}", out int pct) || pct < GuaranteedDropPercent)
                    continue;
                (drops ??= new List<int>()).Add(itemId);
            }
            if (drops is null) continue;

            string name = row.TryGetProperty("Name", out JsonElement nm)
                          && nm.ValueKind == JsonValueKind.String
                ? nm.GetString() ?? string.Empty
                : string.Empty;
            byMonster[id] = (name, drops);
        }
        return byMonster;
    }

    // Resolve each giver monster's spawn rooms off Monsters.json "Summoned By".
    // Only the monsters that actually give an item are looked up — parsing every
    // monster's (often hundreds-long) Summoned By would be wasted work, and the
    // router only ever asks where a known giver lives.
    private void BuildGiverMonsterRooms(
        Dictionary<int, List<ItemGiver>> giversByItem,
        Dictionary<int, List<RoomKey>> giverRoomsByMonster)
    {
        var giverMonsters = new HashSet<int>();
        foreach (List<ItemGiver> givers in giversByItem.Values)
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
                giverRoomsByMonster[num] = rooms;
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

    private static void AddGiver(Dictionary<int, List<ItemGiver>> giversByItem, int itemId, ItemGiver giver)
    {
        if (!giversByItem.TryGetValue(itemId, out List<ItemGiver>? list))
            giversByItem[itemId] = list = new List<ItemGiver>();

        // Dedup by source and keyword — the same monster / room reached through
        // several award lines of one keyword is one row (the hermit's `remind` has a
        // line per alignment path). Keep the first requirement seen, backfilling it
        // if the earlier hit had none, treat the row as deterministic if any award
        // line to it is (a reliable hand-over exists even if another line is gated),
        // and collect every line's refusal messages. A row with no keyword is the
        // same give seen before its keyword was known, so it takes the keyword.
        for (int i = 0; i < list.Count; i++)
        {
            ItemGiver e = list[i];
            if (e.Kind == giver.Kind && e.Number == giver.Number
                && e.Map == giver.Map && e.Room == giver.Room
                && (e.Keyword.Length == 0 || giver.Keyword.Length == 0
                    || string.Equals(e.Keyword, giver.Keyword, StringComparison.OrdinalIgnoreCase)))
            {
                if (e.Requirement.Length == 0 && giver.Requirement.Length > 0)
                    e = e with { Requirement = giver.Requirement };
                if (e.Keyword.Length == 0 && giver.Keyword.Length > 0)
                    e = e with { Keyword = giver.Keyword };
                if (!e.Deterministic && giver.Deterministic)
                    e = e with { Deterministic = true };
                // A plain trade only while every line merged into the row is the
                // same one. A conditioned line under the keyword may be the one the
                // game runs, so it voids the trade whichever order they arrive in.
                if (e.TradeItemId != giver.TradeItemId)
                    e = e with { TradeItemId = 0 };
                if (giver.RefusalMessages is { Count: > 0 } more)
                    e = e with { RefusalMessages = (e.RefusalMessages ?? Array.Empty<int>()).Union(more).ToList() };
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

    // True when the token's first word is a reserved directive verb — i.e. the
    // token is a directive, not a player-typed keyword. Tells a give's own trigger
    // (the leading token of a single-block "give blade:...:giveitem 22") from a
    // directive-led award line ("giveitem 807:...", "failability 127:...").
    private static bool LooksLikeDirective(string tok)
    {
        if (tok.Length == 0) return false;
        int space = tok.IndexOf(' ');
        string head = space < 0 ? tok : tok[..space];
        return TBInfoActionResolver.IsDirectiveHead(head);
    }

    // How many of a block's lines one trigger runs: the lines led by that keyword,
    // or for the empty keyword the directive-led lines the block's menu key reaches.
    private static int LinesSharingTrigger(string[] lines, string keyword)
    {
        int count = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string head = line.Split(':', 2)[0].Trim();
            string trigger = LooksLikeDirective(head) ? string.Empty : head;
            if (string.Equals(trigger, keyword, StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    // What an award line may hold and still be a plain exchange: the take, the
    // give, a check for the item about to be taken, and the lines it prints. Any
    // other directive is a condition on the character or a side effect.
    private static readonly HashSet<string> s_exchangeVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "takeitem", "giveitem", "checkitem", "text", "message",
    };

    private static bool IsExchangeToken(string tok)
    {
        int space = tok.IndexOf(' ');
        return s_exchangeVerbs.Contains(space < 0 ? tok : tok[..space]);
    }

    // The conditions whose last argument is the message printed when they fail
    // (GAME_MECHANICS "TBInfo directive fail messages").
    private static readonly HashSet<string> s_refusingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "minlevel", "maxlevel", "goodaligned", "evilaligned", "checkitem", "failitem",
        "failability",
    };

    // The fail-message number a condition token carries ("goodaligned -51 3075" →
    // 3075), or null when it's not such a condition or names no message.
    private static int? RefusalMessageOf(string tok)
    {
        string[] words = tok.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 3 || !s_refusingVerbs.Contains(words[0])) return null;
        return int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n : null;
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
