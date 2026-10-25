using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;

namespace MudPlay.Services;

// The active set's room spells that damage a character who stays in the room, each
// with what RoomSpellDamageClassifier read of it and the rooms that carry it. Backs
// the rule that a rest isn't started in a room whose own spell does damage, and
// Settings → Room Spell Config, where the user says per spell whether it bars
// resting. A spell the user keeps off that list (KeptOffByUser) is left out here
// too, so it never bars a rest.
//
// Rebuilt on GameDataCache.ActiveSetChanged like RoomSpellTeleportIndex, and for the
// same reason subscribed after RoomGraph and TBInfo: it reads the rooms off the
// graph and the textblocks off the TBInfo store.
public sealed class RoomSpellDamageIndex
{
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager _graph;
    private readonly KnownSpellCatalog _spells;
    private readonly TBInfoStore _tbinfo;
    private readonly LogService? _log;

    private Dictionary<int, RoomSpellDamageReading> _readings = new();
    private Dictionary<int, List<Room>> _rooms = new();
    private Dictionary<int, string> _leftOff = new();

    private const string OnlyWithAMove = "its damage only comes with a move out of the room";
    private const string ClassFilter = "a class filter, which moves the wrong class out of the area";
    private const string PyramidTrap = "a Great Pyramid trap";

    // Room spells kept off the list though some of their damage lands on a character
    // who stays (Client policy, user, 2026-10-24; GAME_MECHANICS "Resting in a room
    // whose spell does damage"). Nothing in the data marks them out: a `class N:` line
    // ahead of a `teleport` also picks the solo trainer's key check. So they are named,
    // by number and by the name Stock and Paradigm both give the spell, and another
    // set's spell of the same number is left alone.
    //   - The class filters: the spellcaster filter's shard trap hurts an allowed
    //     caster who fails its test, and the user wants none of them listed.
    //   - The Great Pyramid's traps: level 1's is the timer that moves the party out;
    //     level 2's only do damage, at random, and nothing prevents them.
    private static readonly Dictionary<int, (string Name, string Why)> KeptOffByUser = new()
    {
        [1325] = ("thief  filter", ClassFilter),
        [1326] = ("battle  filter", ClassFilter),
        [1327] = ("magic filter", ClassFilter),
        [1337] = ("spellcaster filter", ClassFilter),
        [691] = ("cleanup", PyramidTrap),
        [692] = ("cleanup 2", PyramidTrap),
        [700] = ("cleanup 3", PyramidTrap),
    };

    public RoomSpellDamageIndex(
        GameDataCache cache, RoomGraphManager graph, KnownSpellCatalog spells, TBInfoStore tbinfo, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(tbinfo);
        _cache = cache;
        _graph = graph;
        _spells = spells;
        _tbinfo = tbinfo;
        _log = log;
    }

    // A rest is barred by default where the damage comes with every cast, or is
    // sure to come when a timer the cast started runs out (held breath ends in
    // drowning, which kills). Not on a roll: a rest in such a room is broken now and
    // then and still recovers (user, 2026-10-10: such rooms "only break resting if
    // the damage roll is what is chosen"). Not on a condition: this character may
    // never meet it.
    public static bool BarsRestingByDefault(RoomSpellDamage kind) =>
        kind is RoomSpellDamage.EveryTick or RoomSpellDamage.AfterATimer;

    // Pass Room.Spell. None for a spell no room carries or that does no damage.
    public RoomSpellDamage ClassOf(int spell) =>
        _readings.TryGetValue(spell, out RoomSpellDamageReading? found) ? found.Kind : RoomSpellDamage.None;

    // Every damaging room spell of the set, by spell number.
    public IReadOnlyDictionary<int, RoomSpellDamageReading> Readings => _readings;

    // Why a room spell that does damage is not in Readings, null for any other: its
    // damage only comes with a move out of the room, or the user keeps it off.
    public string? LeftOffBecause(int spell) => _leftOff.GetValueOrDefault(spell);

    // The rooms whose spell this is; empty for a spell that isn't in Readings.
    public IReadOnlyList<Room> RoomsOf(int spell) =>
        _rooms.TryGetValue(spell, out List<Room>? rooms) ? rooms : [];

    // Whether a rest is barred in a room with this spell: the user's choice for the
    // spell when they made one (Settings → Room Spell Config stores only
    // the spells set away from the default), else the default for its class. chosen
    // is asked only for a spell that damages.
    public bool BarsResting(int spell, Func<IReadOnlyDictionary<int, bool>> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        RoomSpellDamage kind = ClassOf(spell);
        if (kind == RoomSpellDamage.None) return false;
        return chosen().TryGetValue(spell, out bool bars) ? bars : BarsRestingByDefault(kind);
    }

    private string? KeptOffWhy(int spell) =>
        KeptOffByUser.TryGetValue(spell, out (string Name, string Why) kept)
        && string.Equals(_spells.GetSpellNameByNumber(spell), kept.Name, StringComparison.OrdinalIgnoreCase)
            ? kept.Why
            : null;

    // Reload for setName; null clears. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        var readings = new Dictionary<int, RoomSpellDamageReading>();
        var rooms = new Dictionary<int, List<Room>>();
        var gaps = new List<string>();
        var leftOff = new Dictionary<int, string>();
        var leftOffNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(setName))
        {
            foreach (IGrouping<int, Room> carrying in _graph.Rooms.Where(static r => r.Spell > 0)
                .GroupBy(static r => r.Spell).OrderBy(static g => g.Key))
            {
                RoomSpellDamageReading reading = RoomSpellDamageClassifier.Classify(
                    carrying.Key, _spells.GetFormulaByNumber, _tbinfo.GetEntry);
                if (reading.Gap is not null) gaps.Add($"{carrying.Key} ({reading.Gap})");
                string? why = reading.OnlyWithAMove ? OnlyWithAMove
                    : reading.Kind != RoomSpellDamage.None ? KeptOffWhy(carrying.Key) : null;
                if (why is not null)
                {
                    leftOff[carrying.Key] = why;
                    leftOffNames.Add($"{_spells.GetSpellNameByNumber(carrying.Key) ?? "room spell"} #{carrying.Key} ({why})");
                    continue;
                }
                if (reading.Kind == RoomSpellDamage.None) continue;
                readings[carrying.Key] = reading;
                rooms[carrying.Key] = carrying.ToList();
            }
            // The catalog read the raw Spells table to resolve the chains.
            _cache.EvictTable("Spells");
        }
        _readings = readings;
        _rooms = rooms;
        _leftOff = leftOff;

        if (string.IsNullOrWhiteSpace(setName))
            _log?.Info("RoomSpellDamageIndex", "No active set; cleared.");
        else
        {
            int Count(RoomSpellDamage kind) => readings.Values.Count(r => r.Kind == kind);
            _log?.Info("RoomSpellDamageIndex",
                $"Room spells in '{setName}' that damage: {Count(RoomSpellDamage.EveryTick)} on every tick and "
                + $"{Count(RoomSpellDamage.AfterATimer)} on a timer (a rest is barred there unless "
                + $"Settings → Room Spell Config says otherwise), {Count(RoomSpellDamage.OnARoll)} on a roll, "
                + $"{Count(RoomSpellDamage.Conditional)} on a condition.");
            if (leftOffNames.Count > 0)
                _log?.Info("RoomSpellDamageIndex",
                    $"Room spell(s) that damage left off the list, so they never bar a rest: {string.Join("; ", leftOffNames)}.");
            // A gap is data the set lacks, so it is the same every load: said once here.
            if (gaps.Count > 0)
                _log?.Info("RoomSpellDamageIndex", $"Room spell(s) not read in full: {string.Join("; ", gaps)}.");
        }
    }
}
