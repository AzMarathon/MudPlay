using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// The arithmetic behind HangupItemRecheck: what is held, what of an earlier list
// is short now, and which of the short items a floor list shows. Counted by item
// name over worn, carried, lit and key-ring copies together, so a piece that only
// changed place (a weapon in the pack that was in hand) is not missing.
public static class HangupItemPlan
{
    // Everything the snapshot holds, one row per item name. Coins are no part of
    // it: the inventory record keeps them apart, and the penalty takes items.
    public static List<HeldItem> Held(InventorySnapshot snapshot)
    {
        List<HeldItem> rows = new();
        foreach (EquippedItem worn in snapshot.EquippedItems)
            if (!string.IsNullOrWhiteSpace(worn.Name)) RowFor(rows, worn.Name).Count++;
        foreach (string carried in snapshot.CarriedItems)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(carried);
            if (name.Length > 0) RowFor(rows, name).Count += count;
        }
        foreach (string key in snapshot.Keys ?? Array.Empty<string>())
        {
            (int count, string name) = InventorySnapshot.ParseKeyEntry(key);
            if (name.Length > 0) RowFor(rows, name).Count += count;
        }
        if (snapshot.ReadiedLight is { Name.Length: > 0 } light)
            RowFor(rows, light.Name).Count++;
        return rows;
    }

    // The rows of `before` the snapshot holds fewer copies of, each with how many
    // it is short.
    public static List<(string Name, int Count)> Missing(IReadOnlyList<HeldItem> before, InventorySnapshot now)
    {
        List<HeldItem> held = Held(now);
        List<(string Name, int Count)> missing = new();
        foreach (HeldItem was in before)
        {
            int shortBy = was.Count - (Find(held, was.Name)?.Count ?? 0);
            if (shortBy > 0) missing.Add((was.Name, shortBy));
        }
        return missing;
    }

    // What to get: each missing item the floor list shows, as many copies as are
    // both missing and lying there. Nothing else on the floor is touched.
    //   floor — "You notice" entries as the game words them ("2 torch", "a rusty dagger").
    //   key   — one spelling for a held name and a floor entry of the same item;
    //           the item table's where there is one, else the name without its
    //           article and count.
    public static List<(string Name, int Count)> Pickup(
        IReadOnlyList<(string Name, int Count)> missing, IReadOnlyList<string> floor,
        Func<string, string>? key = null)
    {
        key ??= ItemNameStore.Normalize;
        Dictionary<string, int> lying = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in floor)
        {
            string k = key(entry);
            if (k.Length > 0) lying[k] = lying.GetValueOrDefault(k) + AutoGetItemsManager.ParseLeadingCount(entry);
        }

        List<(string Name, int Count)> plan = new();
        foreach ((string name, int count) in missing)
        {
            string k = key(name);
            int take = Math.Min(count, lying.GetValueOrDefault(k));
            if (take <= 0) continue;
            lying[k] -= take;
            plan.Add((name, take));
        }
        return plan;
    }

    // The plan cut to `most` copies in all, in its own order. The list a check
    // compares can be older than it looks (torches burnt since the last `i`, a
    // save from before a death), and what makes a like-named item on the floor
    // the character's own is only that the board can have dropped it: so never
    // more than the board drops.
    public static List<(string Name, int Count)> Capped(IReadOnlyList<(string Name, int Count)> plan, int most)
    {
        List<(string Name, int Count)> capped = new();
        foreach ((string name, int count) in plan)
        {
            int take = Math.Min(count, most);
            if (take <= 0) break;
            most -= take;
            capped.Add((name, take));
        }
        return capped;
    }

    // `missing` less the copies picked up so far.
    public static List<(string Name, int Count)> Outstanding(
        IReadOnlyList<(string Name, int Count)> missing, IReadOnlyDictionary<string, int> taken)
    {
        List<(string Name, int Count)> left = new();
        foreach ((string name, int count) in missing)
        {
            int still = count - taken.GetValueOrDefault(name);
            if (still > 0) left.Add((name, still));
        }
        return left;
    }

    // The held list with items still missing put back on it. A list saved while a
    // check is under way would otherwise forget them, and a second hang-up before
    // they are picked up would leave them on the floor for good.
    public static List<HeldItem> WithOutstanding(
        List<HeldItem> held, IReadOnlyList<(string Name, int Count)> outstanding)
    {
        foreach ((string name, int count) in outstanding) RowFor(held, name).Count += count;
        return held;
    }

    private static HeldItem? Find(List<HeldItem> rows, string name) =>
        rows.Find(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private static HeldItem RowFor(List<HeldItem> rows, string name)
    {
        if (Find(rows, name) is { } row) return row;
        row = new HeldItem(name, count: 0);
        rows.Add(row);
        return row;
    }
}
