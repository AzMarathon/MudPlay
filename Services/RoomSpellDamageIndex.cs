using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;

namespace MudPlay.Services;

// The active set's room spells that do damage, each with what RoomSpellDamageClassifier
// read of it and the rooms that carry it. Backs the rule that a rest isn't started in
// a room whose own spell does damage, and Settings → Periodic Damage Room Spells,
// where the user says per spell whether it bars resting.
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

    // A rest is barred by default only where the damage comes with every cast: a
    // rest in a room that hurts on a roll is broken now and then and still recovers
    // (user, 2026-10-10: such rooms "only break resting if the damage roll is what
    // is chosen"), and a condition may never be met by this character.
    public static bool BarsRestingByDefault(RoomSpellDamage kind) => kind == RoomSpellDamage.EveryTick;

    // Pass Room.Spell. None for a spell no room carries or that does no damage.
    public RoomSpellDamage ClassOf(int spell) =>
        _readings.TryGetValue(spell, out RoomSpellDamageReading? found) ? found.Kind : RoomSpellDamage.None;

    // Every damaging room spell of the set, by spell number.
    public IReadOnlyDictionary<int, RoomSpellDamageReading> Readings => _readings;

    // The rooms whose spell this is; empty for a spell that isn't in Readings.
    public IReadOnlyList<Room> RoomsOf(int spell) =>
        _rooms.TryGetValue(spell, out List<Room>? rooms) ? rooms : [];

    // Whether a rest is barred in a room with this spell: the user's choice for the
    // spell when they made one (Settings → Periodic Damage Room Spells stores only
    // the spells set away from the default), else the default for its class. chosen
    // is asked only for a spell that damages.
    public bool BarsResting(int spell, Func<IReadOnlyDictionary<int, bool>> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        RoomSpellDamage kind = ClassOf(spell);
        if (kind == RoomSpellDamage.None) return false;
        return chosen().TryGetValue(spell, out bool bars) ? bars : BarsRestingByDefault(kind);
    }

    // Reload for setName; null clears. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        var readings = new Dictionary<int, RoomSpellDamageReading>();
        var rooms = new Dictionary<int, List<Room>>();
        var gaps = new List<string>();
        if (!string.IsNullOrWhiteSpace(setName))
        {
            foreach (IGrouping<int, Room> carrying in _graph.Rooms.Where(static r => r.Spell > 0)
                .GroupBy(static r => r.Spell).OrderBy(static g => g.Key))
            {
                RoomSpellDamageReading reading = RoomSpellDamageClassifier.Classify(
                    carrying.Key, _spells.GetFormulaByNumber, _tbinfo.GetEntry);
                if (reading.Gap is not null) gaps.Add($"{carrying.Key} ({reading.Gap})");
                if (reading.Kind == RoomSpellDamage.None) continue;
                readings[carrying.Key] = reading;
                rooms[carrying.Key] = carrying.ToList();
            }
            // The catalog read the raw Spells table to resolve the chains.
            _cache.EvictTable("Spells");
        }
        _readings = readings;
        _rooms = rooms;

        if (string.IsNullOrWhiteSpace(setName))
            _log?.Info("RoomSpellDamageIndex", "No active set; cleared.");
        else
        {
            int Count(RoomSpellDamage kind) => readings.Values.Count(r => r.Kind == kind);
            _log?.Info("RoomSpellDamageIndex",
                $"Room spells in '{setName}' that damage: {Count(RoomSpellDamage.EveryTick)} on every tick (a rest is barred there unless "
                + $"Settings → Periodic Damage Room Spells says otherwise), {Count(RoomSpellDamage.OnARoll)} on a roll, "
                + $"{Count(RoomSpellDamage.Conditional)} on a condition.");
            // A gap is data the set lacks, so it is the same every load: said once here.
            if (gaps.Count > 0)
                _log?.Info("RoomSpellDamageIndex", $"Room spell(s) not read in full: {string.Join("; ", gaps)}.");
        }
    }
}
