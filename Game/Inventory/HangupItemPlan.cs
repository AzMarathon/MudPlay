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
        {
            if (string.IsNullOrWhiteSpace(worn.Name)) continue;
            HeldItem row = RowFor(rows, worn.Name);
            row.Count++;
            (row.WornSlots ??= new List<string>()).Add(worn.Slot);
        }
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

    // The rows of `before` the snapshot holds fewer copies of. A worn copy counts
    // as missing from its slot only as far as the item is short at all.
    public static List<HangupMissingItem> Missing(IReadOnlyList<HeldItem> before, InventorySnapshot now)
    {
        List<HeldItem> held = Held(now);
        List<HangupMissingItem> missing = new();
        foreach (HeldItem was in before)
        {
            HeldItem? still = Find(held, was.Name);
            int shortBy = was.Count - (still?.Count ?? 0);
            if (shortBy <= 0) continue;

            List<string> slots = new(was.WornSlots ?? new List<string>());
            foreach (string slot in still?.WornSlots ?? new List<string>())
            {
                if (slots.Count == 0) break;
                int at = slots.IndexOf(slot);
                slots.RemoveAt(at >= 0 ? at : 0);
            }
            if (slots.Count > shortBy) slots.RemoveRange(shortBy, slots.Count - shortBy);
            missing.Add(new HangupMissingItem(was.Name, shortBy, slots));
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
        IReadOnlyList<HangupMissingItem> missing, IReadOnlyList<string> floor, Func<string, string>? key = null)
    {
        key ??= ItemNameStore.Normalize;
        Dictionary<string, int> lying = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in floor)
        {
            string k = key(entry);
            if (k.Length > 0) lying[k] = lying.GetValueOrDefault(k) + AutoGetItemsManager.ParseLeadingCount(entry);
        }

        List<(string Name, int Count)> plan = new();
        foreach (HangupMissingItem item in missing)
        {
            string k = key(item.Name);
            int take = Math.Min(item.Count, lying.GetValueOrDefault(k));
            if (take <= 0) continue;
            lying[k] -= take;
            plan.Add((item.Name, take));
        }
        return plan;
    }

    // `missing` less the copies picked up so far, the worn ones counted back first.
    public static List<HangupMissingItem> Outstanding(
        IReadOnlyList<HangupMissingItem> missing, IReadOnlyDictionary<string, int> taken)
    {
        List<HangupMissingItem> left = new();
        foreach (HangupMissingItem item in missing)
        {
            int got = Math.Min(item.Count, taken.GetValueOrDefault(item.Name));
            if (got >= item.Count) continue;
            left.Add(new HangupMissingItem(item.Name, item.Count - got, item.WornSlots.Skip(got).ToList()));
        }
        return left;
    }

    // The held list with items still missing put back on it. A list saved while a
    // check is under way would otherwise forget them, and a second hang-up before
    // they are picked up would leave them on the floor for good.
    public static List<HeldItem> WithOutstanding(List<HeldItem> held, IReadOnlyList<HangupMissingItem> outstanding)
    {
        foreach (HangupMissingItem item in outstanding)
        {
            HeldItem row = RowFor(held, item.Name);
            row.Count += item.Count;
            if (item.WornSlots.Count > 0) (row.WornSlots ??= new List<string>()).AddRange(item.WornSlots);
        }
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
