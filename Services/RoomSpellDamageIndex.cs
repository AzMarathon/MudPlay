using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;

namespace MudPlay.Services;

// The active set's room spells, each classed by whether it damages whoever stands in
// the room (RoomSpellDamageClassifier). Backs the rule that a rest isn't started in
// a room whose own spell does damage.
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

    private Dictionary<int, RoomSpellDamage> _classes = new();

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

    // Pass Room.Spell. True only for a spell read as damaging every time it is cast;
    // one no room carries, or whose damage hangs on a roll or a condition, is not.
    public bool DamagesEveryTick(int spell) =>
        _classes.TryGetValue(spell, out RoomSpellDamage found) && found == RoomSpellDamage.EveryTick;

    public RoomSpellDamage ClassOf(int spell) =>
        _classes.TryGetValue(spell, out RoomSpellDamage found) ? found : RoomSpellDamage.None;

    // Reload for setName; null clears. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        var classes = new Dictionary<int, RoomSpellDamage>();
        var gaps = new List<string>();
        if (!string.IsNullOrWhiteSpace(setName))
        {
            foreach (int spell in _graph.Rooms.Select(static r => r.Spell).Where(static s => s > 0).Distinct().Order())
            {
                RoomSpellDamage found = RoomSpellDamageClassifier.Classify(
                    spell, _spells.GetFormulaByNumber, _tbinfo.GetEntry, out string? gap);
                if (found != RoomSpellDamage.None) classes[spell] = found;
                if (gap is not null) gaps.Add($"{spell} ({gap})");
            }
            // The catalog read the raw Spells table to resolve the chains.
            _cache.EvictTable("Spells");
        }
        _classes = classes;

        if (string.IsNullOrWhiteSpace(setName))
            _log?.Info("RoomSpellDamageIndex", "No active set; cleared.");
        else
        {
            int Count(RoomSpellDamage kind) => classes.Values.Count(c => c == kind);
            _log?.Info("RoomSpellDamageIndex",
                $"Room spells in '{setName}' that damage: {Count(RoomSpellDamage.EveryTick)} on every tick (no rest is started there), "
                + $"{Count(RoomSpellDamage.OnARoll)} on a roll, {Count(RoomSpellDamage.Conditional)} on a condition.");
            // A gap is data the set lacks, so it is the same every load: said once here.
            if (gaps.Count > 0)
                _log?.Info("RoomSpellDamageIndex", $"Room spell(s) not read in full: {string.Join("; ", gaps)}.");
        }
    }
}
