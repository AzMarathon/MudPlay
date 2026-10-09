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

    // Pass Room.Spell. A spell no room carries, or 0, reads as no teleport.
    public RoomSpellTeleport ClassOf(int spell) =>
        _classes.TryGetValue(spell, out RoomSpellTeleport found) ? found : RoomSpellTeleport.None;

    // Reload for setName; null clears. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        var classes = new Dictionary<int, RoomSpellTeleport>();
        if (!string.IsNullOrWhiteSpace(setName))
        {
            foreach (int spell in _graph.Rooms.Select(static r => r.Spell).Where(static s => s > 0).Distinct())
                classes[spell] = RoomSpellTeleportClassifier.Classify(
                    spell, _spells.GetFormulaByNumber, n => _tbinfo.GetEntry(n)?.Action);
            // The catalog read the raw Spells table to resolve the chains.
            _cache.EvictTable("Spells");
        }
        _classes = classes;

        int Count(RoomSpellTeleport kind) => classes.Values.Count(c => c == kind);
        _log?.Info("RoomSpellTeleportIndex", string.IsNullOrWhiteSpace(setName)
            ? "No active set; cleared."
            : $"Classed {classes.Count} room spell(s) from '{setName}': {Count(RoomSpellTeleport.Always)} teleport, "
              + $"{Count(RoomSpellTeleport.Chance)} may teleport, {Count(RoomSpellTeleport.None)} don't.");

        StoreReloaded?.Invoke();
    }
}
