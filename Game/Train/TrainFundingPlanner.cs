using System;
using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Game.Train;

// Decides how a train gets paid for, before a single step is walked.
//
// The order is the user's, not a cost optimisation, and it is deliberate:
//
//   1. Purse covers it        → no detour at all.
//   2. One stash covers it    → nearest such stash. Coin we already hid on the
//                               circuit is cheaper to collect than a bank trip,
//                               and it is coin doing nothing where it lies.
//   3. One bank covers it     → the branch nearest THE TRAINER, not nearest to
//                               us: the withdraw is a detour on the way to train,
//                               so what matters is the walk we still owe after it.
//   4. Combine                → stashes nearest-first, then banks, until covered.
//
// Pure: the caller injects the distance metric (BFS) and the candidate list, so
// the whole decision table is testable without a map, a wire, or a purse.
public static class TrainFundingPlanner
{
    // Cap on stops. Three separate errands to afford one train is already a long
    // way off the grind; past that the walking costs more than the level is worth
    // and something is wrong with how the character is banking money.
    public const int MaxLegs = 3;

    public static TrainFundingPlan Plan(
        long cost,
        long onHandCopper,
        IReadOnlyList<TrainFundingSource> sources,
        RoomKey from,
        RoomKey trainer,
        Func<RoomKey, RoomKey, int?> distance,
        int maxLegs = MaxLegs)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(distance);

        long shortfall = cost - onHandCopper;
        if (shortfall <= 0)
            return new(true, cost, onHandCopper, Array.Empty<TrainFundingLeg>(), 0, 0);

        // Only sources we can actually reach, and that hold something. An
        // unreachable stash is worth exactly as much as an empty one.
        List<TrainFundingSource> reachable = new();
        foreach (TrainFundingSource s in sources)
            if (s.AvailableCopper > 0 && distance(from, s.Room) is not null)
                reachable.Add(s);

        // 2 + 3: a single source that settles the whole shortfall on its own.
        if (SingleSourceCovering(reachable, shortfall, from, trainer, distance) is { } solo)
            return new(true, cost, onHandCopper,
                new[] { new TrainFundingLeg(solo.Kind, solo.Room, solo.Name, shortfall) }, 0,
                solo.Kind == TrainFundingSourceKind.Stash ? shortfall : 0);

        // 4: nothing covers it alone — accumulate, stashes first.
        return Combine(cost, onHandCopper, shortfall, reachable, from, trainer, distance, maxLegs);
    }

    // The best single source that fully covers the shortfall, honouring the
    // stash-before-bank preference: a qualifying stash wins outright even when a
    // bank sits closer, because the stash coin is ours-in-hand once collected and
    // the bank trip is an extra errand we would rather not owe.
    private static TrainFundingSource? SingleSourceCovering(
        IReadOnlyList<TrainFundingSource> sources, long shortfall,
        RoomKey from, RoomKey trainer, Func<RoomKey, RoomKey, int?> distance)
    {
        // Stashes rank by how far out of our way they are; banks rank by their
        // distance to the trainer (see the header).
        TrainFundingSource? bestStash = null, bestBank = null;
        int stashScore = int.MaxValue, bankScore = int.MaxValue;

        foreach (TrainFundingSource s in sources)
        {
            if (s.AvailableCopper < shortfall) continue;
            if (s.Kind == TrainFundingSourceKind.Stash)
            {
                int score = Detour(from, s.Room, trainer, distance);
                if (score < stashScore) { stashScore = score; bestStash = s; }
            }
            else
            {
                int score = distance(s.Room, trainer) ?? int.MaxValue;
                if (score < bankScore) { bankScore = score; bestBank = s; }
            }
        }
        return bestStash ?? bestBank;
    }

    private static TrainFundingPlan Combine(
        long cost, long onHand, long shortfall,
        IReadOnlyList<TrainFundingSource> sources,
        RoomKey from, RoomKey trainer, Func<RoomKey, RoomKey, int?> distance,
        int maxLegs)
    {
        List<TrainFundingLeg> legs = new();
        List<TrainFundingSource> pool = new(sources);
        RoomKey cursor = from;
        long remaining = shortfall;
        long speculative = 0;

        while (remaining > 0 && legs.Count < maxLegs && pool.Count > 0)
        {
            int pick = -1;
            int bestScore = int.MaxValue;
            for (int i = 0; i < pool.Count; i++)
            {
                // Stash-before-bank again: bias banks behind every stash so the
                // combined route empties our own caches before drawing a balance.
                int score = Detour(cursor, pool[i].Room, trainer, distance);
                if (pool[i].Kind == TrainFundingSourceKind.Bank) score += BankBias;
                if (score < bestScore) { bestScore = score; pick = i; }
            }
            if (pick < 0) break;

            TrainFundingSource s = pool[pick];
            pool.RemoveAt(pick);
            long draw = Math.Min(s.AvailableCopper, remaining);
            legs.Add(new(s.Kind, s.Room, s.Name, draw));
            if (s.Kind == TrainFundingSourceKind.Stash) speculative += draw;
            remaining -= draw;
            cursor = s.Room;
        }

        return new(remaining <= 0, cost, onHand, legs, Math.Max(0, remaining), speculative);
    }

    // Enough to lose any realistic hop-count tie, small enough that it never
    // reorders two sources of the same kind.
    private const int BankBias = 10_000;

    // Extra steps visiting `via` adds to the trip we already owe. Unreachable
    // legs score as unusable rather than as free.
    private static int Detour(RoomKey from, RoomKey via, RoomKey to, Func<RoomKey, RoomKey, int?> distance)
    {
        if (distance(from, via) is not { } a) return int.MaxValue;
        int b = distance(via, to) ?? 0;
        return a + b;
    }
}
