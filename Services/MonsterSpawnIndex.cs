using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Reverse index of RoomKey → monster ids whose Monsters.json Summoned By
// field references that room. Builds once on the active game-data set and
// rebuilds on GameDataCache.ActiveSetChanged.
//
// Bosses and other script-spawned monsters carry their spawn site on the
// monster record rather than the room's Room.RawLairTag. The room search
// reads this via NavigationViewModel.RoomsByMonsterId; the room tooltip needs
// the inverse — given a room, which monsters does it host? Without the index
// a tooltip's Also Here line silently omits any boss whose presence lives
// only on the monster record (live repro: 1/1678 Darkwood Forest, Webbed
// Clearing had no giant spider in its tooltip even though Monster 52's
// Summoned By reads "Room 1/1678").
//
// The Summoned By field mixes THREE kinds of room reference, distinguished by
// the keyword before the map/room token (verified against the data + the room
// side: a monster's `Group(lair)` token always points at a room WITH a Lair
// tag, `Group:` at one WITHOUT):
//   - "Room m/r"          → the room's NPC fixture — a PLACED boss / unique.
//   - "Group(lair): m/r"  → a LAIR spawn (the room's Lair tag lists it too).
//   - "Group: m/r"        → an ASSIGNED roam / rare-random spawn (no lair).
// We index placed and assigned separately so the tooltip can show the
// distinction; lair members are read from the room's own Lair tag, so the
// (lair) tokens aren't split out here. `MonsterIdsSummonedAt` keeps returning
// EVERY token (the combat resolver's permissive candidate set relies on it).
//
// Cache rebuild costs O(monsters) once per active-set switch; per-tooltip
// lookups are O(1). The Lairs.json GroupIndex tokens use '-' between numbers,
// so a digits/digits regex catches only room references.
public sealed class MonsterSpawnIndex
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, List<int>> _summonedAt = new();
    private readonly Dictionary<RoomKey, List<int>> _placedAt = new();
    private readonly Dictionary<RoomKey, List<int>> _assignedAt = new();
    private bool _built;

    private static readonly Regex s_roomTokenRegex
        = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    public MonsterSpawnIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
        _cache.ActiveSetChanged += _ => Invalidate();
    }

    // Monster ids whose Summoned By references the given room (ALL token kinds —
    // placed, assigned, and lair). Empty list when nothing spawns at the room (or
    // no Monsters table is loaded). Builds the index lazily on first call after a
    // set change. This is the combat resolver's permissive candidate set.
    public IReadOnlyList<int> MonsterIdsSummonedAt(RoomKey key)
    {
        EnsureBuilt();
        return _summonedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Monster ids PLACED at the room — a "Room m/r" token (the room's NPC
    // fixture: bosses / uniques). Excludes lair and assigned/roam spawns.
    public IReadOnlyList<int> PlacedMonsterIdsAt(RoomKey key)
    {
        EnsureBuilt();
        return _placedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Monster ids ASSIGNED to roam / rare-randomly spawn at the room — a
    // non-lair "Group: m/r" token. Excludes placed fixtures and lair spawns.
    public IReadOnlyList<int> AssignedMonsterIdsAt(RoomKey key)
    {
        EnsureBuilt();
        return _assignedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Drops the cached index — the next lookup re-scans Monsters.json.
    public void Invalidate()
    {
        _summonedAt.Clear();
        _placedAt.Clear();
        _assignedAt.Clear();
        _built = false;
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                "Active set has no Monsters.json; index left empty.");
            return;
        }

        int linked = 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl)
                || numEl.ValueKind != JsonValueKind.Number
                || !numEl.TryGetInt32(out int id)) continue;
            if (!row.TryGetProperty("Summoned By", out JsonElement summonEl)
                || summonEl.ValueKind != JsonValueKind.String) continue;
            string? text = summonEl.GetString();
            if (string.IsNullOrEmpty(text)) continue;

            // Each comma-separated part carries at most one "map/room" token plus
            // the keyword that classifies it. Split first so the keyword and its
            // room stay associated (a bare regex sweep loses that pairing).
            foreach (string part in text.Split(','))
            {
                Match m = s_roomTokenRegex.Match(part);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int map) || map <= 0) continue;
                if (!int.TryParse(m.Groups[2].Value, out int room) || room <= 0) continue;
                RoomKey key = new(map, room);

                AddTo(_summonedAt, key, id);   // permissive set — every token
                linked++;

                bool isLair = part.Contains("(lair)", StringComparison.OrdinalIgnoreCase);
                if (isLair) continue;          // lair members come from the room's Lair tag
                if (part.Contains("Group", StringComparison.OrdinalIgnoreCase))
                    AddTo(_assignedAt, key, id);   // "Group:" (non-lair) → roam / random
                else
                    AddTo(_placedAt, key, id);     // "Room m/r" (or an unkeyworded ref) → placed fixture
            }
        }

        // Folded into the spawn maps — release the pinned raw Monsters JsonDocument.
        _cache.EvictTable("Monsters");
        _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
            $"Built spawn index — {_summonedAt.Count} room(s) host {linked} monster reference(s) "
            + $"({_placedAt.Count} placed, {_assignedAt.Count} assigned).");
    }

    private static void AddTo(Dictionary<RoomKey, List<int>> map, RoomKey key, int id)
    {
        if (!map.TryGetValue(key, out List<int>? list))
            map[key] = list = new List<int>();
        if (!list.Contains(id)) list.Add(id);
    }
}
