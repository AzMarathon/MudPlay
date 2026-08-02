using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Turns a loop's waypoints into the ordered ExpRoute the LoopExpSimulator
// replays: BFS-expands the cycle to its room sequence, then reads each room's
// lair and NPC-fixture exp yield + respawn timer from game data. Reuses
// LoopExpander (route), LairTimerStore (respawn seconds — already realm-aware)
// and the raw Monsters / Lairs tables. The monster index is lazy and drops when
// the active set changes.
//
// A room can contribute two targets: its lair group AND a separately-placed NPC
// fixture (e.g. the cave-worm room). Only exp-bearing targets are emitted —
// a 0-exp fixture (the barmaid) is left out so it doesn't tax lap time for no
// gain. See GAME_MECHANICS.md "Lair respawn timers & NPC-placed monsters".
public sealed class RouteExpResolver : IDisposable
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LairTimerStore _timers;
    private readonly GameDataCache _cache;

    private Dictionary<int, int>? _monsters;   // monster Number -> EXP

    public RouteExpResolver(RoomGraphManager graph, BfsMapper bfs, LairTimerStore timers, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(cache);
        _graph = graph;
        _bfs = bfs;
        _timers = timers;
        _cache = cache;
        _cache.ActiveSetChanged += OnSetChanged;
    }

    public void Dispose() => _cache.ActiveSetChanged -= OnSetChanged;

    private void OnSetChanged(string? _)
    {
        _monsters = null;
    }

    public ExpRoute Resolve(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        IReadOnlyList<RoomKey> keys = LoopExpander.ResolveCycleRoomKeys(waypoints, _bfs, _graph, filter);
        if (keys.Count < 2) return new ExpRoute(Array.Empty<ExpRoomVisit>());

        // keys ends back at the start; drop that closing duplicate — the wrap
        // step is implicit in the simulator's lap.
        int lapCount = keys.Count - 1;
        var lap = new List<ExpRoomVisit>(lapCount);
        for (int i = 0; i < lapCount; i++)
            lap.Add(new ExpRoomVisit(keys[i], ResolveTargets(keys[i])));
        return new ExpRoute(lap);
    }

    private IReadOnlyList<ExpTarget> ResolveTargets(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return Array.Empty<ExpTarget>();
        List<ExpTarget>? targets = null;

        if (room.Npc > 0)
        {
            // NPC-placed fixture: respawns instantly on entry (RespawnSeconds 0),
            // regardless of the monster's RegenTime — that timer governs LAIR
            // respawn, not fixtures. See GAME_MECHANICS.md.
            int exp = Monster(room.Npc);
            if (exp > 0) (targets ??= new List<ExpTarget>()).Add(new ExpTarget(1, exp, 0));
        }
        if (room.HasLair && LairYield(room.RawLairTag) is (int mobs, double expPerMob) && expPerMob > 0)
        {
            int respawn = _timers.DefaultRespawnSeconds(key) ?? 0;
            (targets ??= new List<ExpTarget>()).Add(new ExpTarget(mobs, expPerMob, respawn));
        }
        return targets ?? (IReadOnlyList<ExpTarget>)Array.Empty<ExpTarget>();
    }

    // Parse the raw MDB lair cell "(Max N): id,id,...,[group-maxregen]" → the
    // per-room max simultaneous spawn count and the average EXP of the listed
    // monsters. This is the on-disk shape for these sets; LairTagParser targets
    // a different (transformed) format the data doesn't use, which is why the
    // monster ids are read straight from the cell here.
    private (int Mobs, double ExpPerMob)? LairYield(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        Match maxM = Regex.Match(raw, @"Max\s+(\d+)");
        int mobs = maxM.Success && int.TryParse(maxM.Groups[1].Value, out int mv) && mv > 0 ? mv : 1;

        int colon = raw.IndexOf(':');
        if (colon < 0) return null;
        int bracket = raw.IndexOf('[', colon);
        int end = bracket > colon ? bracket : raw.Length;

        long sum = 0;
        int n = 0;
        foreach (string tok in raw[(colon + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(tok, out int id) && id > 0) { sum += Monster(id); n++; }
        }
        return n > 0 ? (mobs, (double)sum / n) : null;
    }

    private int Monster(int id) => Monsters().TryGetValue(id, out int exp) ? exp : 0;

    private Dictionary<int, int> Monsters()
    {
        if (_monsters is not null) return _monsters;
        var map = new Dictionary<int, int>();
        if (_cache.GetRawTable("Monsters") is { } doc && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement n) || !n.TryGetInt32(out int id)) continue;
                int exp = row.TryGetProperty("EXP", out JsonElement e) && e.TryGetInt32(out int ev) ? ev : 0;
                map[id] = exp;
            }
        }
        return _monsters = map;
    }
}
