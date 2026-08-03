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

    private Dictionary<int, MonsterInfo>? _monsters;   // monster Number -> resolved info

    // Per-monster facts the estimator needs. Exp is the true value EXP × ExpMulti
    // (bosses store the un-multiplied EXP with a separate multiplier). A boss —
    // GameLimit 1 or a RegenTime of an hour or more — is killable only once per its
    // regen, so it's pulled out of the lair average and amortised over RegenHours.
    private readonly record struct MonsterInfo(int Exp, bool IsBoss, int RegenHours, string Name);

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
            MonsterInfo npc = Monster(room.Npc);
            if (npc.Exp > 0)
            {
                if (npc.IsBoss)
                    // A PLACED boss (GameLimit 1 / regen ≥ 1h) is killable only once
                    // per its regen even as a fixture — amortise it like a lair boss,
                    // not an every-pass instant fixture (e.g. animated juggernaut
                    // 17/7055: 1.3M, 3h regen → +433k/hr, once).
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                        1, npc.Exp, npc.RegenHours * 3600, MonsterId: room.Npc, IsBoss: true, MonsterName: npc.Name));
                else
                    // Normal NPC fixture: respawns instantly on entry (RespawnSeconds
                    // 0), regardless of RegenTime — that governs LAIR respawn, not
                    // fixtures. See GAME_MECHANICS.md.
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(1, npc.Exp, 0));
            }
        }
        if (room.HasLair && ParseLair(room.RawLairTag) is (int mobs, List<int> ids))
        {
            int respawn = _timers.DefaultRespawnSeconds(key) ?? 0;
            // Split the lair: bosses (GameLimit 1 / long regen) come out of the
            // average and become their own once-per-regen target; the rest average
            // as normal lair mobs on the room's respawn.
            long sum = 0;
            int n = 0;
            foreach (int id in ids)
            {
                MonsterInfo m = Monster(id);
                if (m.Exp <= 0) continue;
                if (m.IsBoss)
                {
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                        1, m.Exp, m.RegenHours * 3600, MonsterId: id, IsBoss: true, MonsterName: m.Name));
                    continue;
                }
                sum += m.Exp;
                n++;
            }
            if (n > 0)
                (targets ??= new List<ExpTarget>()).Add(new ExpTarget(mobs, (double)sum / n, respawn));
        }
        return targets ?? (IReadOnlyList<ExpTarget>)Array.Empty<ExpTarget>();
    }

    // Parse the raw MDB lair cell "(Max N): id,id,...,[group-maxregen]" → the
    // per-room max simultaneous spawn count and the listed monster ids. This is the
    // on-disk shape for these sets; LairTagParser targets a different (transformed)
    // format the data doesn't use, which is why the ids are read straight from the
    // cell here. Boss-vs-regular split + averaging happens in ResolveTargets.
    private static (int Mobs, List<int> Ids)? ParseLair(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        Match maxM = Regex.Match(raw, @"Max\s+(\d+)");
        int mobs = maxM.Success && int.TryParse(maxM.Groups[1].Value, out int mv) && mv > 0 ? mv : 1;

        int colon = raw.IndexOf(':');
        if (colon < 0) return null;
        int bracket = raw.IndexOf('[', colon);
        int end = bracket > colon ? bracket : raw.Length;

        var ids = new List<int>();
        foreach (string tok in raw[(colon + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(tok, out int id) && id > 0) ids.Add(id);
        return ids.Count > 0 ? (mobs, ids) : null;
    }

    private MonsterInfo Monster(int id) => Monsters().TryGetValue(id, out MonsterInfo m) ? m : default;

    private Dictionary<int, MonsterInfo> Monsters()
    {
        if (_monsters is not null) return _monsters;
        var map = new Dictionary<int, MonsterInfo>();
        if (_cache.GetRawTable("Monsters") is { } doc && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement n) || !n.TryGetInt32(out int id)) continue;
                int baseExp = row.TryGetProperty("EXP", out JsonElement e) && e.TryGetInt32(out int ev) ? ev : 0;
                // True exp is EXP × ExpMulti (the boss/exp multiplier). The Game-Data
                // browser already displays it this way; the estimator read only EXP
                // before, so a ×20 boss showed 1/20th its real value.
                int multi = row.TryGetProperty("ExpMulti", out JsonElement em) && em.TryGetInt32(out int mvv) && mvv > 0 ? mvv : 1;
                int regen = row.TryGetProperty("RegenTime", out JsonElement rt) && rt.TryGetInt32(out int rv) ? rv : 0;
                int limit = row.TryGetProperty("GameLimit", out JsonElement gl) && gl.TryGetInt32(out int lv) ? lv : 0;
                string name = row.TryGetProperty("Name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? string.Empty : string.Empty;
                bool isBoss = limit == 1 || regen >= 1;   // RegenTime is in HOURS
                map[id] = new MonsterInfo(baseExp * multi, isBoss, Math.Max(1, regen), name);
            }
        }
        return _monsters = map;
    }
}
