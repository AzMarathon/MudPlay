using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;

namespace MudPlay.Services;

// The active set's room-entry spells, each classed by whether it teleports
// (RoomSpellTeleportClassifier). Backs the Navigation map's by-teleport overlay and
// the hover tooltip's room-spell line.
//
// Rebuilt on GameDataCache.ActiveSetChanged like RoomHazardIndex. It reads the
// rooms off the graph and the textblocks off the TBInfo store, so AppServices
// subscribes it after both: by the time it runs they hold the new set.
public sealed class RoomSpellTeleportIndex
{
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager _graph;
    private readonly KnownSpellCatalog _spells;
    private readonly TBInfoStore _tbinfo;
    private readonly LogService? _log;

    // Replaced whole on every reload, never edited, so the map can tell a reload
    // from the reference changing.
    private IReadOnlyDictionary<int, RoomSpellTeleport> _classes = new Dictionary<int, RoomSpellTeleport>();

    // Every spell placed on a room in the active set, by spell number.
    public IReadOnlyDictionary<int, RoomSpellTeleport> Classes => _classes;

    // Fires after every reload, including the transition to no-set-active.
    public event Action? StoreReloaded;

    public RoomSpellTeleportIndex(
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

    // Pass Room.Spell. A spell no room carries reads as unknown, never as a spell
    // without a teleport: nothing was read to say so.
    public RoomSpellTeleport ClassOf(int spell) =>
        _classes.TryGetValue(spell, out RoomSpellTeleport found) ? found : RoomSpellTeleport.Unknown;

    // Reload for setName; null clears. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        var classes = new Dictionary<int, RoomSpellTeleport>();
        var gaps = new List<string>();
        if (!string.IsNullOrWhiteSpace(setName))
        {
            foreach (int spell in _graph.Rooms.Select(static r => r.Spell).Where(static s => s > 0).Distinct().Order())
            {
                RoomSpellTeleport found = RoomSpellTeleportClassifier.Classify(
                    spell, _spells.GetFormulaByNumber, _tbinfo.GetEntry, out string? gap);
                classes[spell] = found;
                if (gap is not null)
                    gaps.Add($"{spell} ({gap}; {(found == RoomSpellTeleport.Unknown ? "unknown" : "teleport found anyway")})");
            }
            // The catalog read the raw Spells table to resolve the chains.
            _cache.EvictTable("Spells");
        }
        _classes = classes;

        if (string.IsNullOrWhiteSpace(setName))
            _log?.Info("RoomSpellTeleportIndex", "No active set; cleared.");
        else
        {
            int Count(RoomSpellTeleport kind) => classes.Values.Count(c => c == kind);
            _log?.Info("RoomSpellTeleportIndex",
                $"Classed {classes.Count} room spell(s) from '{setName}': {Count(RoomSpellTeleport.Sudden)} teleport outright or on a roll, "
                + $"{Count(RoomSpellTeleport.Conditional)} on a condition, {Count(RoomSpellTeleport.None)} don't, "
                + $"{Count(RoomSpellTeleport.Unknown)} unknown.");
            // A gap is data the set lacks, so it is the same every load: said once
            // here, where a "why isn't this room green" report can find it.
            if (gaps.Count > 0)
                _log?.Info("RoomSpellTeleportIndex", $"Room spell(s) not read in full: {string.Join("; ", gaps)}.");
        }

        StoreReloaded?.Invoke();
    }
}
