using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Inventory;

// Pure helpers behind the Chest Offload window: diff a before/after carried
// inventory to find what a chest gave, and pack the sellable loot into as few
// shops as possible. Everything here is deterministic and service-free so it can
// be unit-tested; the view-model supplies the item→shop and pricing lookups.
public static class ChestOffloadPlanner
{
    // Per-item count gains between two carried-item lists (the display tokens from
    // an InventorySnapshot, e.g. "3 piece of amber"). Counts are count-prefix aware
    // (CountedCommand.SplitLeadingCount), grouped by the bare singular name. Only
    // positive deltas are returned — a consumed container (before 1, after 0) and
    // anything sold in between drop out. Order follows first appearance in `after`.
    public static IReadOnlyList<(string Name, int Count)> CarriedGains(
        IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        Dictionary<string, int> beforeCounts = CountByName(before);
        Dictionary<string, int> afterCounts = CountByName(after);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gains = new List<(string, int)>();
        foreach (string token in after)
        {
            (int _, string name) = CountedCommand.SplitLeadingCount(token);
            if (!seen.Add(name)) continue;   // one entry per distinct name
            int delta = afterCounts.GetValueOrDefault(name) - beforeCounts.GetValueOrDefault(name);
            if (delta > 0) gains.Add((name, delta));
        }
        return gains;
    }

    private static Dictionary<string, int> CountByName(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in tokens)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(token);
            counts[name] = counts.GetValueOrDefault(name) + count;
        }
        return counts;
    }

    // Assign each item to exactly one shop, minimising the number of distinct shops
    // to visit (a greedy set-cover: repeatedly take the shop that covers the most
    // still-unassigned items). Items whose candidate-shop set is empty are returned
    // in `unassigned`. `shopsFor` gives an item's candidate shop keys; ties break on
    // the lowest shop key so the result is deterministic.
    public static IReadOnlyList<(int Shop, IReadOnlyList<T> Items)> GroupByFewestShops<T>(
        IReadOnlyList<T> items,
        Func<T, IReadOnlyCollection<int>> shopsFor,
        out IReadOnlyList<T> unassigned)
    {
        var candidates = new List<(T Item, HashSet<int> Shops)>();
        var noShop = new List<T>();
        foreach (T item in items)
        {
            var shops = new HashSet<int>(shopsFor(item));
            if (shops.Count == 0) noShop.Add(item);
            else candidates.Add((item, shops));
        }
        unassigned = noShop;

        var result = new List<(int Shop, IReadOnlyList<T> Items)>();
        var remaining = candidates;
        while (remaining.Count > 0)
        {
            // Tally how many remaining items each candidate shop could take.
            var coverage = new Dictionary<int, int>();
            foreach ((_, HashSet<int> shops) in remaining)
                foreach (int shop in shops)
                    coverage[shop] = coverage.GetValueOrDefault(shop) + 1;

            int bestShop = coverage
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .First().Key;

            var taken = new List<T>();
            var rest = new List<(T, HashSet<int>)>();
            foreach ((T item, HashSet<int> shops) in remaining)
            {
                if (shops.Contains(bestShop)) taken.Add(item);
                else rest.Add((item, shops));
            }
            result.Add((bestShop, taken));
            remaining = rest;
        }
        return result;
    }
}
