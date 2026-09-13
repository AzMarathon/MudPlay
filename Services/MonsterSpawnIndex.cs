using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Reverse index of RoomKey → monster ids whose Monsters.json Summoned By
// field references that room. One immutable build per active set, published
// by a single reference assignment so a background warm-up can run safely —
// the same pattern ItemSourceIndex uses. Warmed on a worker thread when the
// active set changes (Services.AppServices); a query that beats the warm
// simply builds it itself.
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
// Cache rebuild costs O(monster references) once per active-set switch;
// per-tooltip lookups are O(1). The Lairs.json GroupIndex tokens use '-'
// between numbers, so a digits/digits regex catches only room references.
public sealed class MonsterSpawnIndex
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    // One immutable build, published by a single reference assignment. Readers
    // take the current snapshot and work from it, so a rebuild can run on a
    // background thread without ever handing a caller a half-filled map.
    private sealed class Snapshot
    {
        public required string? Set { get; init; }
        public required Dictionary<RoomKey, List<int>> SummonedAt { get; init; }
        public required Dictionary<RoomKey, List<int>> PlacedAt { get; init; }
        public required Dictionary<RoomKey, List<int>> AssignedAt { get; init; }
    }

    // Serialises builders so two threads racing the same set build once, not twice.
    private readonly object _buildGate = new();
    private volatile Snapshot? _snapshot;

    private static readonly Regex s_roomTokenRegex
        = new(@"(\d+)/(\d+)", RegexOptions.Compiled);

    public MonsterSpawnIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Monster ids whose Summoned By references the given room (ALL token kinds —
    // placed, assigned, and lair). Empty list when nothing spawns at the room (or
    // no Monsters table is loaded). This is the combat resolver's permissive
    // candidate set. Live view — read, don't mutate.
    public IReadOnlyList<int> MonsterIdsSummonedAt(RoomKey key)
    {
        Snapshot snap = EnsureBuilt();
        return snap.SummonedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Monster ids PLACED at the room — a "Room m/r" token (the room's NPC
    // fixture: bosses / uniques). Excludes lair and assigned/roam spawns.
    // Live view — read, don't mutate.
    public IReadOnlyList<int> PlacedMonsterIdsAt(RoomKey key)
    {
        Snapshot snap = EnsureBuilt();
        return snap.PlacedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Monster ids ASSIGNED to roam / rare-randomly spawn at the room — a
    // non-lair "Group: m/r" token. Excludes placed fixtures and lair spawns.
    // Live view — read, don't mutate.
    public IReadOnlyList<int> AssignedMonsterIdsAt(RoomKey key)
    {
        Snapshot snap = EnsureBuilt();
        return snap.AssignedAt.TryGetValue(key, out List<int>? list) ? list : Array.Empty<int>();
    }

    // Build the index for the active set ahead of anyone asking for it. Safe to
    // call from a worker thread — the build touches only locals until it
    // publishes, and GameDataCache guards its own tables — so the build lands
    // while the set loads instead of inside the first room-tooltip hover that
    // needs it. A query racing the warm just builds it itself; the gate makes
    // one of them wait rather than doing the work twice.
    public void Warm()
    {
        try
        {
            // mayEvictRaw: false — see EnsureBuilt. Reading the shared table off
            // this thread is fine; disposing it is not.
            EnsureBuilt(mayEvictRaw: false);
        }
        catch (Exception ex)
        {
            // A warm-up is an optimisation; a failure here must not take down
            // the thread it runs on. The next query rebuilds and surfaces the fault.
            _log?.Log(LogSeverity.Warn, "MonsterSpawnIndex",
                $"Warm-up failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    // mayEvictRaw gates the post-build EvictTable("Monsters"). GameDataCache
    // DISPOSES the JsonDocument on evict, and ~20 other consumers read that same
    // shared document from the UI thread — so evicting from the warm's worker
    // thread can pull it out from under a UI-thread enumeration mid-flight
    // (ObjectDisposedException on whichever index happened to be mid-build).
    // Reading it off-thread is what ItemSourceIndex's warm already does and is
    // safe; disposing it isn't. Only a build on the caller's own thread evicts —
    // and the raw table still gets reclaimed either way, since MonsterCatalog,
    // RoomGraphManager, SeeHiddenIndex and friends all evict "Monsters" too.
    private Snapshot EnsureBuilt(bool mayEvictRaw = true)
    {
        string? active = _cache.ActiveSet;
        if (_snapshot is { } current && current.Set == active) return current;

        lock (_buildGate)
        {
            // Another thread may have published while we waited for the gate.
            if (_snapshot is { } published && published.Set == active) return published;
            Snapshot built = Build(active, mayEvictRaw);
            _snapshot = built;
            return built;
        }
    }

    private Snapshot Build(string? active, bool mayEvictRaw)
    {
        var summonedAt = new Dictionary<RoomKey, List<int>>();
        var placedAt = new Dictionary<RoomKey, List<int>>();
        var assignedAt = new Dictionary<RoomKey, List<int>>();

        JsonDocument? doc = string.IsNullOrWhiteSpace(active) ? null : _cache.GetRawTable("Monsters");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                "Active set has no Monsters.json; index left empty.");
        }
        else
        {
            // Per-room dedup trackers, one HashSet alongside each List<int> above —
            // discarded once the build returns. A room with many "Summoned By" hits
            // (a common "Group: m/r" roam token shared by thousands of monster
            // records) used to dedup via List<int>.Contains, an O(n) scan on every
            // insert that made a single hot room's build cost O(n²). On Paradigm's
            // ~215k-token Monsters.json that was a real ~29 s UI-thread stall
            // (paradigm-20260911-231808); a HashSet makes the check O(1).
            var summonedSeen = new Dictionary<RoomKey, HashSet<int>>();
            var placedSeen = new Dictionary<RoomKey, HashSet<int>>();
            var assignedSeen = new Dictionary<RoomKey, HashSet<int>>();

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

                    AddTo(summonedAt, summonedSeen, key, id);   // permissive set — every token
                    linked++;

                    bool isLair = part.Contains("(lair)", StringComparison.OrdinalIgnoreCase);
                    if (isLair) continue;          // lair members come from the room's Lair tag
                    if (part.Contains("Group", StringComparison.OrdinalIgnoreCase))
                        AddTo(assignedAt, assignedSeen, key, id);   // "Group:" (non-lair) → roam / random
                    else
                        AddTo(placedAt, placedSeen, key, id);       // "Room m/r" (or an unkeyworded ref) → placed fixture
                }
            }

            // Folded into the spawn maps — release the pinned raw Monsters
            // JsonDocument, but only when it's safe to dispose it here.
            if (mayEvictRaw) _cache.EvictTable("Monsters");
            _log?.Log(LogSeverity.Info, "MonsterSpawnIndex",
                $"Built spawn index — {summonedAt.Count} room(s) host {linked} monster reference(s) "
                + $"({placedAt.Count} placed, {assignedAt.Count} assigned).");
        }

        return new Snapshot
        {
            Set = active,
            SummonedAt = summonedAt,
            PlacedAt = placedAt,
            AssignedAt = assignedAt,
        };
    }

    // Append id to map[key], first-seen order, using the paired seen[key] set for
    // an O(1) "already have it" check instead of scanning the growing list.
    private static void AddTo(
        Dictionary<RoomKey, List<int>> map, Dictionary<RoomKey, HashSet<int>> seen, RoomKey key, int id)
    {
        if (!seen.TryGetValue(key, out HashSet<int>? set))
            seen[key] = set = new HashSet<int>();
        if (!set.Add(id)) return;

        if (!map.TryGetValue(key, out List<int>? list))
            map[key] = list = new List<int>();
        list.Add(id);
    }
}
