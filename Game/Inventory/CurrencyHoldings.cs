namespace FujinTerm.Game.Inventory;

// Per-denomination coin counts the player is carrying, plus the consolidated
// wealth value. Counts are individual coins, not values (e.g. Gold = 30 means
// thirty gold crowns, worth 3000 copper). The MajorMUD ratios used to derive
// TotalCopperValue are 1 silver = 10 copper, 1 gold = 100, 1 platinum = 10000,
// 1 runic = 1000000, so encumbrance/wealth math stays faithful.
//
// TotalCopperValue is consolidated wealth in copper farthings — authoritative
// on a full 'i' parse (read from the game's Wealth: line), recomputed from the
// per-coin counts on incremental pickup/drop.
public readonly record struct CurrencyHoldings(
    int Copper,
    int Silver,
    int Gold,
    int Platinum,
    int Runic,
    long TotalCopperValue)
{
    // All-zero holdings (never observed).
    public static CurrencyHoldings Empty => new(0, 0, 0, 0, 0, 0);

    // Total physical coin count across every denomination — the input to the
    // 3-coins-per-encumbrance-unit weight rule.
    public long TotalCoinCount => (long)Copper + Silver + Gold + Platinum + Runic;

    // Copper-farthing value of count coins of the named single-word
    // denomination, using the same MajorMUD ratio ladder as TotalCopperValue.
    // Unrecognised names yield 0 so callers can fold mixed currency streams
    // without a separate validity check.
    public static long ToCopper(string currency, long count) =>
        currency.ToLowerInvariant() switch
        {
            "copper"   => count,
            "silver"   => count * 10,
            "gold"     => count * 100,
            "platinum" => count * 10_000,
            "runic"    => count * 1_000_000,
            _          => 0,
        };

    // Decompose the coin above a raw copper-farthing keep floor into a set of
    // per-denomination offload commands. Hides the LOWEST denominations first
    // (copper → runic), so the coins left on hand are the fewest possible: for a
    // fixed value to shed, cheap coins are the most numerous per unit of value,
    // so dumping them first leaves the highest-value coins behind. The result
    // sits at the keep floor (or just above it, when discrete coins can't split
    // to land exactly) with the minimum coin count. Used by the stash path,
    // where `hide N <coin>` names a specific denomination and so needs the raw
    // floor converted back into concrete coins to dump.
    //
    // Wealth is summed from the per-coin counts here (not TotalCopperValue) so
    // the plan is realizable coin-by-coin: we only ever offload denominations we
    // actually hold. excess = held - max(0, keepCopper); each step hides as many
    // of the current denomination as fit inside the remaining excess without
    // dropping below the floor. Returns the canonical "runic" name — callers map
    // it to the board-specific word at send time. Empty when nothing sits above
    // the floor.
    public IReadOnlyList<(string Currency, long Count)> PlanOffloadAboveKeep(long keepCopper)
    {
        long held =
            ToCopper("copper", Copper)
            + ToCopper("silver", Silver)
            + ToCopper("gold", Gold)
            + ToCopper("platinum", Platinum)
            + ToCopper("runic", Runic);

        long excess = held - Math.Max(0, keepCopper);
        if (excess <= 0)
            return Array.Empty<(string, long)>();

        List<(string Currency, long Count)> plan = new();
        foreach ((string name, long unit, long available) in new[]
        {
            ("copper", 1L, (long)Copper),
            ("silver", 10L, (long)Silver),
            ("gold", 100L, (long)Gold),
            ("platinum", 10_000L, (long)Platinum),
            ("runic", 1_000_000L, (long)Runic),
        })
        {
            if (excess <= 0)
                break;

            long count = Math.Min(available, excess / unit);
            if (count <= 0)
                continue;

            plan.Add((name, count));
            excess -= count * unit;
        }

        return plan;
    }
}
