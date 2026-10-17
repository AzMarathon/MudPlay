using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// Who can cast each spell of the active set: the monsters that carry it, the room
// spells that lead to it, and whether those are all there is. A spell reached only
// through another one (blizzard ends in freezing, freezing water in holding breath
// and then drowning: an EndCast, ability 151) has that spell's casters.
//
// Backs ConditionTracker's reading of an applied line several spells print: a
// record whose spell only monsters and rooms cast, with none of them where the
// character stands, did not print it (CastReach).
//
// Each kind of caster is read from the table that holds it, because the Spells
// table's own "Casted By" column is cut off at 250 characters (107 spells on
// Paradigm 1.9.1, 46 on Stock 1.11p) and leaves casters out even where it is not:
//   * the Monsters table for who casts what;
//   * the rooms' own Spell for the room half;
//   * what the client can't see cast, which leaves the spell open: it may have
//     come from somewhere not in view, and is never ruled out. That is a player
//     who learned it ("Learnable", "Learned From", or taught by an item's LearnSp
//     slot or a textblock's `learnspell`: a player's cast, not the item's or the
//     textblock's; a hostile one is read apart, below), an item's cast (its CastsSp
//     slots), a textblock's `cast` step,
//     an exit's pre- or post-cast or spell trap, another spell's enchantment cast
//     (ability 43) or temporary grant (160), a monster's death or spawn spell, and
//     an EndCast from a spell with a duration, which lands when that spell ends,
//     its caster maybe long gone.
// "Casted By" is then a cross-check: a source it names that the tables don't
// account for (a "Room" entry that is not that room's own spell, a "Spell" entry
// that is not an EndCast, a monster the set has no row for) still leaves the spell
// open. Where it is cut off, the tables stand for the rest, save for a spell in
// the MinBase to MaxBase of a spell with an EndCast of 0: only the list names that
// tie, so a list that may have lost it leaves the spell open.
//
// One kind of player cast is not left open: a learned hostile spell, one with an
// enemy scope (BuffClassifier.IsEnemyScope), taught by an item or a textblock or
// not. A party member's never lands on the
// character, and another player's only on a PvP realm (user, 2026-10-17), so it is
// in reach only of a player outside the party in the room (PlayersAtFoes). A
// player's heal or buff can land on anyone, the character's own among them, and
// stays open.
//
// Built on a worker when a set's room graph has loaded (Warm), never by a lookup:
// Of is asked while a game line is being read, and answers only for the load it
// was built for.
public sealed class SpellCasterIndex
{
    // Monsters holds names, not numbers: the roster knows a monster by the name the
    // game printed, and several records can share one.
    // OnlyMonstersAndRooms: every caster of the spell is in the two sets, so with
    // none of them present the spell was not cast here.
    // PlayersAtFoes: the casters are the two sets and players who learned the spell,
    // which is hostile; the two never both hold.
    public sealed record Casters(
        IReadOnlySet<string> Monsters, IReadOnlySet<int> RoomSpells, bool OnlyMonstersAndRooms,
        bool PlayersAtFoes);

    private sealed record Snapshot(
        GameDataCache.Load Load, Dictionary<int, Casters> BySpell, HashSet<string> MonsterNames);

    // How many spells each source the client can't see cast names, for the log.
    private sealed class Tally
    {
        public readonly HashSet<int> Items = new();
        public readonly HashSet<int> Textblocks = new();
        public readonly HashSet<int> Taught = new();
        public readonly HashSet<int> Exits = new();
        public readonly HashSet<int> Spells = new();
        public int LandLater;
    }

    private const int AbilLearnSp = 42;
    private const int AbilCastsSp = 43;
    private const int AbilEndCast = 151;
    private const int AbilRemovesSpell = 122;
    private const int AbilKillSpell = 153;
    private const int AbilGiveTempSpell = 160;
    private const int SpellAbilSlots = 10;
    private const int ItemAbilSlots = 20;
    private const int AttackSlots = 5;
    private const int MidSpellSlots = 5;
    private static readonly string[] AbilFields = NumberedFields.Of("Abil-", SpellAbilSlots);
    private static readonly string[] AbilValFields = NumberedFields.Of("AbilVal-", SpellAbilSlots);
    private static readonly string[] ItemAbilFields = NumberedFields.Of("Abil-", ItemAbilSlots);
    private static readonly string[] ItemAbilValFields = NumberedFields.Of("AbilVal-", ItemAbilSlots);
    private static readonly string[] AttTypeFields = NumberedFields.Of("AttType-", AttackSlots);
    private static readonly string[] AttAccFields = NumberedFields.Of("AttAcc-", AttackSlots);
    private static readonly string[] AttPercentFields = NumberedFields.Of("Att%-", AttackSlots);
    private static readonly string[] AttHitSpellFields = NumberedFields.Of("AttHitSpell-", AttackSlots);
    private static readonly string[] MidSpellFields = NumberedFields.Of("MidSpell-", MidSpellSlots);

    private static readonly IReadOnlySet<string> NoMonsters = new HashSet<string>();
    private static readonly IReadOnlySet<int> NoRoomSpells = new HashSet<int>();

    private readonly GameDataCache _cache;
    private readonly Func<IEnumerable<Room>> _rooms;
    private readonly LogService? _log;
    private readonly TBInfoStore? _textblocks;
    // One build at a time, so the build of the later load is the one left standing.
    private readonly object _buildGate = new();
    private volatile Snapshot? _snapshot;

    // rooms: the active set's rooms, as the room graph holds them. textblocks: the
    // active set's TBInfo entries; without them no textblock is known to cast.
    public SpellCasterIndex(
        GameDataCache cache, Func<IEnumerable<Room>> rooms, LogService? log = null, TBInfoStore? textblocks = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(rooms);
        _cache = cache;
        _rooms = rooms;
        _log = log;
        _textblocks = textblocks;
    }

    // The spell's casters; null for a spell the set doesn't have, and while the
    // index is not built for the load the cache is serving.
    public Casters? Of(int spell) =>
        Current() is { } built && built.BySpell.TryGetValue(spell, out Casters? casters) ? casters : null;

    // Whether the set has a monster of this name. A roster entry it has none for
    // can't be asked what it casts.
    public bool KnowsMonster(string name) =>
        Current() is { } built && built.MonsterNames.Contains(name);

    private Snapshot? Current() =>
        _snapshot is { } built && built.Load == _cache.ActiveLoad ? built : null;

    // Build for the load the cache is serving. For a worker thread: it reads four
    // tables and the textblocks, and walks the room graph.
    public void Warm()
    {
        try
        {
            lock (_buildGate)
            {
                // Read before the build: a switch that overtakes it leaves a result
                // no lookup will take for the new set's.
                GameDataCache.Load load = _cache.ActiveLoad;
                // The cache's load moves before the textblock store reloads. A build
                // begun in between would read the last set's textblocks; it is left
                // unbuilt, and the graph load that follows the store's warms again.
                if (load.Set is not null && _textblocks is not null && _textblocks.LoadedFor != load)
                {
                    _log?.Debug("SpellCasterIndex", "build skipped: the textblocks are not yet this set's");
                    return;
                }
                Stopwatch clock = Stopwatch.StartNew();
                Tally tally = new();
                Snapshot built = Build(load, tally);
                _snapshot = built;
                if (load.Set is not null)
                    _log?.Info("SpellCasterIndex",
                        $"Spell casters in '{load.Set}': {built.BySpell.Count} spells, "
                        + $"{built.BySpell.Values.Count(static c => c.OnlyMonstersAndRooms)} cast only by monsters or rooms, "
                        + $"{built.BySpell.Values.Count(static c => c.PlayersAtFoes)} by those or a player at a foe "
                        + "(an applied line several spells share is read by which of them is cast where the character stands). "
                        + $"{tally.Taught.Count} taught by items or textblocks, read as learnable. "
                        + $"Never ruled out: {tally.Items.Count} cast by items, "
                        + $"{tally.Textblocks.Count} by textblocks, {tally.Exits.Count} by exits, "
                        + $"{tally.Spells.Count} by other spells' casts and grants, "
                        + $"{tally.LandLater} landing when a spell with a duration ends. "
                        + $"Built in {clock.ElapsedMilliseconds} ms.");
            }
        }
        catch (InvalidOperationException ex)
        {
            // The set switched again and the room graph was cleared under the walk;
            // that load warms its own.
            _log?.Debug("SpellCasterIndex", $"build overtaken by a set switch ({ex.Message})");
        }
    }

    private Snapshot Build(GameDataCache.Load load, Tally tally)
    {
        Dictionary<int, Casters> bySpell = new();
        HashSet<string> monsterNames = new(StringComparer.OrdinalIgnoreCase);
        if (load.Set is null || _cache.GetTable("Spells") is not { } spells)
            return new Snapshot(load, bySpell, monsterNames);

        Dictionary<int, HashSet<string>> castBy = new();   // spell → monsters, by name
        HashSet<int> open = new();                         // spells with a caster not in view
        HashSet<int> learnedAtFoes = new();                // a player's hostile spell
        Dictionary<int, string> nameOf = new();
        ReadMonsters(castBy, open, nameOf, monsterNames);

        Dictionary<RoomKey, int> roomSpell = new();
        foreach (Room room in _rooms())
        {
            if (room.Spell > 0) roomSpell[room.Key] = room.Spell;
            foreach (RoomExit exit in room.Exits.Values)
            {
                if (exit.PreCastSpell > 0) tally.Exits.Add(exit.PreCastSpell);
                if (exit.PostCastSpell > 0) tally.Exits.Add(exit.PostCastSpell);
                if (exit.TrapSpell is > 0 and var trap) tally.Exits.Add(trap);
            }
        }
        HashSet<int> carried = new(roomSpell.Values);

        ReadItems(tally.Items, tally.Taught);
        if (_textblocks is not null)
            foreach (TBInfoEntry entry in _textblocks.Entries)
                if (entry.Action is { } action) ReadTextblockSteps(action, tally.Textblocks, tally.Taught);

        // The chains first: "Casted By" names a spell for more than a cast, and which
        // it is shows only on that spell's own row.
        Dictionary<int, List<int>> parents = new();        // spell → the spells that end in it
        HashSet<(int By, int Spell)> ends = new();          // By takes Spell off: no cast
        HashSet<int> known = new();
        HashSet<int> lasting = new();                      // spells with a duration
        List<(int Min, int Max)> endCastsOfNone = new();   // an EndCast of 0's MinBase..MaxBase
        foreach (GameRow row in spells.Rows)
        {
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;
            known.Add(number);
            if (ReadInt(row, "Dur") > 0) lasting.Add(number);
            for (int i = 0; i < SpellAbilSlots; i++)
            {
                int code = ReadInt(row, AbilFields[i]);
                int value = ReadInt(row, AbilValFields[i]);
                if (code == AbilEndCast && value == 0)
                    endCastsOfNone.Add((ReadInt(row, "MinBase"), ReadInt(row, "MaxBase")));
                if (value <= 0) continue;
                if (code == AbilEndCast) AddTo(parents, value, number);
                else if (code is AbilRemovesSpell or AbilKillSpell) ends.Add((number, value));
                else if (code is AbilCastsSp or AbilGiveTempSpell) tally.Spells.Add(value);
            }
        }
        open.UnionWith(tally.Items);
        open.UnionWith(tally.Textblocks);
        open.UnionWith(tally.Exits);
        open.UnionWith(tally.Spells);

        foreach (GameRow row in spells.Rows)
        {
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;

            if (ReadInt(row, "Learnable") == 1 || Clean(ReadString(row, "Learned From")).Length > 0
                || tally.Taught.Contains(number))
                (BuffClassifier.IsEnemyScope(ReadInt(row, "Targets")) ? learnedAtFoes : open).Add(number);

            foreach (string raw in Clean(ReadString(row, "Casted By")).Split(','))
            {
                string source = Clean(raw);
                if (source.Length == 0) continue;
                // The column's cut ("Monster #1+", "R+"): the tables above stand for
                // whatever it left out, save a tie only the list shows.
                if (source.EndsWith('+'))
                {
                    if (endCastsOfNone.Exists(range => number >= range.Min && number <= range.Max))
                        open.Add(number);
                    continue;
                }
                if (NumberAfter(source, "Monster #") is { } monster && nameOf.TryGetValue(monster, out string? name))
                    AddTo(castBy, number, name);
                else if (NumberAfter(source, "Spell #") is { } other)
                {
                    // An EndCast is in parents already. A spell that only takes this
                    // one off (stop drowning, of holding breath) casts nothing. Any
                    // other tie is one the tables above don't show (an EndCast of 0
                    // names no spell), so a cast nothing here can see coming.
                    bool endsInIt = parents.TryGetValue(number, out List<int>? from) && from.Contains(other);
                    if (!endsInIt && !ends.Contains((other, number))) open.Add(number);
                }
                else if (!IsOwnSpellOfRoom(source, number, roomSpell))
                    open.Add(number);
            }
        }

        foreach (int number in known)
        {
            HashSet<string>? monsters = null;
            HashSet<int>? rooms = null;
            bool closed = true;
            bool landsLater = false;
            bool byPlayers = false;
            foreach (int spell in WithWhatEndsInIt(number, parents))
            {
                if (!known.Contains(spell) || open.Contains(spell)) closed = false;
                // Lands when that spell runs out: whoever cast it need not be here.
                if (spell != number && lasting.Contains(spell)) landsLater = true;
                if (learnedAtFoes.Contains(spell)) byPlayers = true;
                if (castBy.TryGetValue(spell, out HashSet<string>? names))
                    (monsters ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(names);
                if (carried.Contains(spell)) (rooms ??= new HashSet<int>()).Add(spell);
            }
            if (landsLater)
            {
                tally.LandLater++;
                closed = false;
            }
            // A spell nothing is listed for is open: the data is missing, not the
            // caster. One players learn has its casters whatever the lists say.
            bySpell[number] = new Casters(
                monsters ?? NoMonsters, rooms ?? NoRoomSpells,
                OnlyMonstersAndRooms: closed && !byPlayers && (monsters is not null || rooms is not null),
                PlayersAtFoes: closed && byPlayers);
        }

        // Folded into the maps: the cache may drop both tables.
        _cache.EvictTable("Monsters");
        _cache.EvictTable("Spells");
        return new Snapshot(load, bySpell, monsterNames);
    }

    // Read off the Monsters table and not MonsterCatalog: this runs on a worker,
    // and the catalogue is built on first use by whoever asks, with nothing to keep
    // two threads from building it at once. Which slots count is the catalogue's
    // rule (RoomSpellAttributor.CastSpells): an attack slot that can land, by its
    // spell or its on-hit spell; every between-round spell; the spawn and death
    // spells.
    private void ReadMonsters(
        Dictionary<int, HashSet<string>> castBy, HashSet<int> open,
        Dictionary<int, string> nameOf, HashSet<string> monsterNames)
    {
        if (_cache.GetTable("Monsters") is not { } monsters) return;
        foreach (GameRow row in monsters.Rows)
        {
            int number = ReadInt(row, "Number");
            string name = Clean(ReadString(row, "Name"));
            if (number <= 0 || name.Length == 0) continue;
            nameOf[number] = name;
            monsterNames.Add(name);

            for (int i = 0; i < AttackSlots; i++)
            {
                int type = ReadInt(row, AttTypeFields[i]);
                if (type is < 1 or > 3 || ReadInt(row, AttPercentFields[i]) <= 0) continue;
                if (type == 2 && ReadInt(row, AttAccFields[i]) is > 0 and var cast) AddTo(castBy, cast, name);
                if (ReadInt(row, AttHitSpellFields[i]) is > 0 and var onHit) AddTo(castBy, onHit, name);
            }
            for (int i = 0; i < MidSpellSlots; i++)
                if (ReadInt(row, MidSpellFields[i]) is > 0 and var between) AddTo(castBy, between, name);

            // A death spell lands as its caster leaves the roster and a spawn spell
            // before the roster has it, so neither can be ruled out by who is listed.
            foreach (string field in new[] { "CreateSpell", "DeathSpell" })
                if (ReadInt(row, field) is > 0 and var unseen)
                {
                    AddTo(castBy, unseen, name);
                    open.Add(unseen);
                }
        }
    }

    // Every spell an item casts, on use or as a proc (ItemCastSpells), and every one
    // it teaches, which a player then casts as one who learned it.
    private void ReadItems(HashSet<int> casts, HashSet<int> taught)
    {
        // Read back for this build alone, so a table nobody held before it goes
        // back to the cache's idle sweep after.
        bool held = _cache.IsTableLoaded("Items");
        if (_cache.GetTable("Items") is { } items)
            foreach (GameRow row in items.Rows)
            {
                foreach (ItemCastSpells.CastEntry cast in ItemCastSpells.ReadCasts(row))
                    casts.Add(cast.SpellNumber);
                for (int i = 0; i < ItemAbilSlots; i++)
                    if (ReadInt(row, ItemAbilFields[i]) == AbilLearnSp && ReadInt(row, ItemAbilValFields[i]) is > 0 and var spell)
                        taught.Add(spell);
            }
        if (!held) _cache.EvictTable("Items");
    }

    // The spells a textblock's steps cast (`cast N`) and teach (`learnspell N`). Its
    // lines are colon-separated steps, wherever they sit: after a typed keyword, a
    // roll's band or nothing.
    private static void ReadTextblockSteps(string action, HashSet<int> casts, HashSet<int> taught)
    {
        foreach (string line in action.Split('\n'))
            foreach (string raw in line.Split(':'))
            {
                QuestScriptStep step = QuestScriptStep.Parse(raw);
                if (step.Int(0) is not { } spell || spell <= 0) continue;
                if (step.Verb == "cast") casts.Add(spell);
                else if (step.Verb == "learnspell") taught.Add(spell);
            }
    }

    // The spell and every spell that ends in it, however many steps back.
    private static HashSet<int> WithWhatEndsInIt(int spell, Dictionary<int, List<int>> parents)
    {
        HashSet<int> seen = new() { spell };
        Stack<int> pending = new();
        pending.Push(spell);
        while (pending.Count > 0)
            if (parents.TryGetValue(pending.Pop(), out List<int>? from))
                foreach (int parent in from)
                    if (seen.Add(parent)) pending.Push(parent);
        return seen;
    }

    private static void AddTo(Dictionary<int, HashSet<string>> map, int key, string value)
    {
        if (!map.TryGetValue(key, out HashSet<string>? set))
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(value);
    }

    private static void AddTo(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out List<int>? list)) map[key] = list = new List<int>();
        if (!list.Contains(value)) list.Add(value);
    }

    // "Room 6/1139", for a room whose own spell this is. The column says "Room" as
    // well for a spell cast by one of the room's exits or commands, which the exits
    // and textblocks above account for.
    private static bool IsOwnSpellOfRoom(string source, int spell, Dictionary<RoomKey, int> roomSpell) =>
        source.StartsWith("Room ", StringComparison.Ordinal)
        && RoomKey.TryParseWire(source["Room ".Length..], out RoomKey key)
        && roomSpell.TryGetValue(key, out int there) && there == spell;

    // "Monster #108" → 108; null unless the whole of what follows the prefix is a
    // number.
    private static int? NumberAfter(string source, string prefix) =>
        source.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(source.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n : null;

    // An empty cell of these columns holds a NUL, and a cut one ends in one.
    private static string Clean(string? text) => text is null ? string.Empty : text.Trim().Trim('\0').Trim();

    private static int ReadInt(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int n) ? n : 0;

    private static string? ReadString(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
