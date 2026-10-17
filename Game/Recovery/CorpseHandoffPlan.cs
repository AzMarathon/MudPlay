using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Recovery;

// The gives that hand a recovered corpse back to a player, built from what the
// recovery lines said was taken (GAME_MECHANICS "Giving items and coins to another
// player"). Paradigm takes a counted give (`give 2 <item> to <name>`); Stock takes
// one copy a command. Coins go one denomination a command.
public static class CorpseHandoffPlan
{
    public enum Kind { Item, Coin }

    // One command and what it hands over: an item name or a coin's ladder word.
    public readonly record struct Give(string Command, Kind What, string Name, int Count);

    public static IReadOnlyList<Give> Build(
        IEnumerable<CorpseHandoffItem> items, CurrencyHoldings coins, string recipient, bool paradigm, string runicName)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        recipient = recipient.Trim();
        List<Give> gives = new();
        foreach (CorpseHandoffItem item in Merge(items))
        {
            if (paradigm && item.Count > 1)
                gives.Add(new($"give {item.Count} {item.Name} to {recipient}", Kind.Item, item.Name, item.Count));
            else
                for (int i = 0; i < item.Count; i++)
                    gives.Add(new($"give {item.Name} to {recipient}", Kind.Item, item.Name, 1));
        }
        foreach ((string currency, int count) in Denominations(coins))
            if (count > 0)
                gives.Add(new(CoinGiveCommand.For(currency, count, recipient, runicName), Kind.Coin, currency, count));
        return gives;
    }

    // The same name taken twice is one entry; names are matched the way the item
    // store matches them (case and a leading article aside).
    public static List<CorpseHandoffItem> Merge(IEnumerable<CorpseHandoffItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<CorpseHandoffItem> merged = new();
        Dictionary<string, CorpseHandoffItem> byName = new(StringComparer.Ordinal);
        foreach (CorpseHandoffItem item in items)
        {
            if (item.Count <= 0 || string.IsNullOrWhiteSpace(item.Name)) continue;
            string key = Services.ItemNameStore.Normalize(item.Name);
            if (byName.TryGetValue(key, out CorpseHandoffItem? seen)) { seen.Count += item.Count; continue; }
            CorpseHandoffItem copy = new(item.Name.Trim(), item.Count);
            byName[key] = copy;
            merged.Add(copy);
        }
        return merged;
    }

    // Largest first, as the coins are listed everywhere else.
    public static IEnumerable<(string Currency, int Count)> Denominations(CurrencyHoldings c)
    {
        yield return ("runic", c.Runic);
        yield return ("platinum", c.Platinum);
        yield return ("gold", c.Gold);
        yield return ("silver", c.Silver);
        yield return ("copper", c.Copper);
    }

    // The holdings with count coins of a ladder word added (negative to take off),
    // never below zero.
    public static CurrencyHoldings Add(CurrencyHoldings c, string currency, int count)
    {
        static int Sum(int a, int b) => Math.Max(0, a + b);
        CurrencyHoldings next = currency switch
        {
            "runic" => c with { Runic = Sum(c.Runic, count) },
            "platinum" => c with { Platinum = Sum(c.Platinum, count) },
            "gold" => c with { Gold = Sum(c.Gold, count) },
            "silver" => c with { Silver = Sum(c.Silver, count) },
            "copper" => c with { Copper = Sum(c.Copper, count) },
            _ => c,
        };
        long total = 0;
        foreach ((string cur, int n) in Denominations(next)) total += CurrencyHoldings.ToCopper(cur, n);
        return next with { TotalCopperValue = total };
    }
}
