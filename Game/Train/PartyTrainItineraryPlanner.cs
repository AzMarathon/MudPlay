using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;

namespace MudPlay.Game.Train;

// A member this trip trains: where it is now and how far it wants to go this trip
// (its own banked levels, capped by its ceiling and the level-11 party rule).
public readonly record struct PartyTrainee(string Name, int Level, int ClassNumber, int TargetLevel);

// One trainer the trip stops at, the members it trains there, and whether the
// leader trains here (only ever the final stop).
public readonly record struct PartyTrainStop(TrainerShop Trainer, IReadOnlyList<string> Members, bool LeaderTrains);

// Orders the trainer stops for a party-train trip.
//
// Members first, leader last — a confirmed mechanic forces the order: a FOLLOWER's
// `train` drops only that follower (it's re-invited when it re-enters the realm), but
// the LEADER's `train` disbands the whole party. So the leader trains exactly once,
// at the final stop, where every member is standing and can be re-invited; it never
// chains on to another trainer afterwards, or the members would be left behind.
//
// Member stops are greedy: the reachable, allowed trainer that serves the MOST
// members still short of their target goes first (nearest breaks a tie), each served
// member advances as far as that trainer's band takes it, and the next stop is chosen
// from there — so members in different level bands chain across trainers the same
// way a single character's itinerary does (TrainItineraryPlanner).
//
// Pure: the caller injects the distance metric, so it's testable without a map.
public static class PartyTrainItineraryPlanner
{
    // A party trip that needs more than this many trainers has something wrong with
    // it (or is spread across too many bands to be worth one trip).
    public const int MaxStops = 8;

    public static IReadOnlyList<PartyTrainStop> Plan(
        IReadOnlyList<TrainerShop> trainers,
        IReadOnlyList<PartyTrainee> members,
        PartyTrainee? leader,
        IReadOnlyCollection<string> disabled,
        RoomKey from,
        Func<RoomKey, RoomKey, int?> distance)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);

        Dictionary<string, int> projected = members.ToDictionary(
            m => m.Name, m => m.Level, StringComparer.OrdinalIgnoreCase);
        List<PartyTrainStop> stops = new();
        RoomKey cursor = from;

        while (stops.Count < MaxStops)
        {
            List<PartyTrainee> pending = members.Where(m => projected[m.Name] < m.TargetLevel).ToList();
            if (pending.Count == 0) break;

            TrainerShop? best = null;
            int bestCount = 0, bestDist = int.MaxValue;
            foreach (TrainerShop t in trainers)
            {
                if (!t.HasRoom || disabled.Contains(t.RowKey)) continue;
                if (distance(cursor, Room(t)) is not { } dist) continue;
                int count = pending.Count(m => Serves(t, projected[m.Name], m.ClassNumber));
                if (count == 0) continue;
                if (count > bestCount || (count == bestCount && dist < bestDist))
                {
                    best = t;
                    bestCount = count;
                    bestDist = dist;
                }
            }
            if (best is not { } stop) break;   // nobody left is servable — they keep their levels banked

            List<string> served = new();
            foreach (PartyTrainee m in pending)
            {
                if (!Serves(stop, projected[m.Name], m.ClassNumber)) continue;
                projected[m.Name] = Advance(stop, projected[m.Name], m.TargetLevel);
                served.Add(m.Name);
            }
            stops.Add(new PartyTrainStop(stop, served, LeaderTrains: false));
            cursor = Room(stop);
        }

        if (leader is not { } l) return stops;

        // The leader trains at the last stop when that trainer serves it — no extra walk.
        if (stops.Count > 0 && Serves(stops[^1].Trainer, l.Level, l.ClassNumber))
        {
            stops[^1] = stops[^1] with { LeaderTrains = true };
            return stops;
        }

        // Otherwise one more stop for the leader, and any member with levels left
        // that the leader's trainer also serves trains there too.
        RoomKey at = cursor;
        if (TrainerCatalog.SelectNearest(trainers, l.Level, l.ClassNumber, disabled,
                t => distance(at, Room(t))) is { } leaderStop
            && stops.Count < MaxStops)
        {
            List<string> extra = new();
            foreach (PartyTrainee m in members)
            {
                if (projected[m.Name] >= m.TargetLevel) continue;
                if (!Serves(leaderStop, projected[m.Name], m.ClassNumber)) continue;
                projected[m.Name] = Advance(leaderStop, projected[m.Name], m.TargetLevel);
                extra.Add(m.Name);
            }
            stops.Add(new PartyTrainStop(leaderStop, extra, LeaderTrains: true));
        }
        return stops;
    }

    private static bool Serves(TrainerShop t, int level, int classNumber) =>
        t.ServesLevel(level) && t.ServesClass(classNumber);

    // How far this trainer takes a member toward its target — the same per-level
    // ServesLevel walk TrainItineraryPlanner uses, so the two agree on a band's top.
    private static int Advance(TrainerShop t, int level, int target)
    {
        while (level < target && t.ServesLevel(level)) level++;
        return level;
    }

    private static RoomKey Room(TrainerShop t) => new(t.Map, t.Room);
}
