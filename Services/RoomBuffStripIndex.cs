using System.Collections.Generic;
using System.Text.Json;

namespace MudPlay.Services;

// In-memory set of the active game-data set's room-entry spells that STRIP player
// buffs on entry — a room whose cast-on-enter spell (Room.Spell) removes or
// dispels magical effects. Backs the auto-buff suppression gate: re-casting a buff
// in a room that immediately strips it just burns mana, so CastingDirector skips
// the Buffing category while the player stands in such a room. It also tells the
// hazard-buff tracker which rooms end a buff it can only time (StripsBuff): "dying
// or walking through a room that purges buffs, negate magic" (user, 2026-10-10).
//
// A room-entry spell strips buffs when any member of its EndCast chain carries a
// buff-removal ability: RemovesSpell (Abil 122 — removes a specific spell effect)
// or DispellMagic (Abil 73 — dispels magical effects). Both are the game's own
// encoding of "this takes your buffs away", so keying on the data lets the client
// learn strip rooms per set without a hand-maintained list.
//
// Mirrors RoomHazardIndex: subscribes to GameDataCache.ActiveSetChanged, reads
// Rooms (for the room-entry spell set) and Spells (for the ability chains) once,
// builds the set, and evicts the JsonDocuments it touched.
public sealed class RoomBuffStripIndex
{
    private const int SpellAbilSlots = 10;   // Spells: Abil-0..9
    private const int AbilDispelMagic = 73;
    private const int AbilRemovesSpell = 122;
    private const int AbilEndCast = 151;
    private const int MaxChainDepth = 16;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    // Per stripping room-entry spell, what its chain takes off: everything (a
    // DispellMagic ability: negate magic), and the spells its RemovesSpell abilities
    // name.
    private readonly Dictionary<int, (bool Dispels, HashSet<int> Removes)> _stripSpells = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of distinct buff-stripping room-entry spells indexed.
    public int StripSpellCount => _stripSpells.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active.
    public event Action? StoreReloaded;

    public RoomBuffStripIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // True when a room's cast-on-enter spell strips buffs. Pass Room.Spell (0 =
    // no room spell). Benign, unknown, and zero spells all read false.
    public bool StripsBuffs(int spell)
        => spell > 0 && _stripSpells.ContainsKey(spell);

    // True when a room's cast-on-enter spell takes this one buff off: its chain
    // dispels magic, or removes the buff by number. For a buff tracked by its clock
    // alone (no wear-off line to read), where a room that only removes other spells
    // must not count: the drowning cure's rooms share a map with the desert, and
    // take nothing off but drowning.
    public bool StripsBuff(int roomSpell, int buffSpell)
        => roomSpell > 0
           && _stripSpells.TryGetValue(roomSpell, out (bool Dispels, HashSet<int> Removes) strip)
           && (strip.Dispels || strip.Removes.Contains(buffSpell));

    // Reload the index for setName. Pass null to clear. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _stripSpells.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("RoomBuffStripIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? rooms = _cache.GetRawTable("Rooms");
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (rooms is null || spells is null)
        {
            _log?.Info("RoomBuffStripIndex",
                $"Active set '{setName}' missing Rooms/Spells; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        // Only spells that actually appear as a Room.Spell are candidates — this
        // keeps the offensive dispel/attack-spell table out of the scan.
        HashSet<int> roomSpells = CollectRoomSpells(rooms);
        Dictionary<int, int[]> spellAbils = ReadSpellAbils(spells);

        foreach (int spell in roomSpells)
        {
            bool dispels = false;
            HashSet<int> removes = new();
            if (ChainStripsBuffs(spell, 0, spellAbils, new HashSet<int>(), ref dispels, removes))
                _stripSpells[spell] = (dispels, removes);
        }

        _cache.EvictTable("Rooms");
        _cache.EvictTable("Spells");

        _log?.Info("RoomBuffStripIndex",
            $"Indexed {_stripSpells.Count} buff-stripping room-entry spell(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }

    // Depth-first walk of a spell's EndCast chain. Returns true when any chain
    // member carries a buff-removal ability (RemovesSpell / DispellMagic), and says
    // which: dispels for a DispellMagic, removes for the spells a RemovesSpell names.
    private bool ChainStripsBuffs(
        int spell, int depth, Dictionary<int, int[]> spellAbils, HashSet<int> seen,
        ref bool dispels, HashSet<int> removes)
    {
        if (depth > MaxChainDepth || spell <= 0 || !seen.Add(spell)) return false;
        if (!spellAbils.TryGetValue(spell, out int[]? abil)) return false;

        bool strips = false;
        for (int k = 0; k < SpellAbilSlots; k++)
        {
            int a = abil[k * 2];
            int v = abil[k * 2 + 1];
            if (a == AbilDispelMagic)
            {
                strips = true;
                dispels = true;
            }
            else if (a == AbilRemovesSpell)
            {
                strips = true;
                if (v > 0) removes.Add(v);
            }
            else if (a == AbilEndCast && v > 0)
                strips |= ChainStripsBuffs(v, depth + 1, spellAbils, seen, ref dispels, removes);
        }
        return strips;
    }

    private static HashSet<int> CollectRoomSpells(JsonDocument rooms)
    {
        HashSet<int> set = new();
        foreach (JsonElement row in rooms.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (TryReadInt(row, "Spell", out int spell) && spell > 0) set.Add(spell);
        }
        return set;
    }

    // Spell Number → flat [Abil0, Val0, Abil1, Val1, …] slot array (2 * SpellAbilSlots).
    private static Dictionary<int, int[]> ReadSpellAbils(JsonDocument spells)
    {
        Dictionary<int, int[]> map = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            int[] slots = new int[SpellAbilSlots * 2];
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                TryReadInt(row, $"Abil-{k}", out slots[k * 2]);
                TryReadInt(row, $"AbilVal-{k}", out slots[k * 2 + 1]);
            }
            map[number] = slots;
        }
        return map;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
