namespace MudPlay.Models.Profile;

// Per-character "Cash" settings — drives Game.Cash.CashManager's per-currency
// pickup / discard behaviour and auto-deposit trigger. Stored as the "Cash"
// entry in CharacterProfile.Settings.
//
// v1 ships per-currency Policy + a single AutoDepositIfWealthExceeds threshold.
// Encumbrance gates, cascade drop-smaller-for-larger, and the walker-driven
// auto-deposit reroute land as follow-up work on this engine — the foundation
// here lets the user smoke-test the per-currency pickup path end-to-end.
public sealed class CashSettings
{
    // Per-currency pickup behavior.
    public CashPolicy CopperPolicy { get; set; } = CashPolicy.Ignore;
    public CashPolicy SilverPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy GoldPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy PlatinumPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy RunicPolicy { get; set; } = CashPolicy.Collect;

    // Auto-deposit trigger — fire when total held wealth (in the realm's
    // canonical unit, typically gold-equivalent) exceeds this value. 0 disables
    // the trigger. v1 fires the CashManager.AutoDepositRequested event;
    // subscribers wire the walker reroute themselves until the full
    // snapshot-pause-walk-deposit-resume flow ships.
    public long AutoDepositIfWealthExceeds { get; set; }

    // Auto-deposit trigger — fire when the total number of physical coins held
    // (summed across every denomination, regardless of each coin's value)
    // exceeds this count. 0 disables the trigger. Independent of
    // AutoDepositIfWealthExceeds: either gate firing triggers the deposit
    // (OR logic), and the single-fire guard re-arms only once BOTH gates fall
    // back below their thresholds.
    public long AutoDepositIfCoinsExceed { get; set; }

    // Bank room key — used by the (follow-up) auto-deposit walker reroute to
    // know where to walk. Sourced from the Shops table where ShopType == 7
    // (bank). v1 just stores it; the reroute itself is unwired.
    public string BankRoomKey { get; set; } = string.Empty;

    // ----- Wealth to keep on hand ------------------------------------
    // The floor the character keeps after offloading coin, expressed as a
    // single raw copper-farthing value — the SAME unit as the Wealth line
    // and AutoDepositIfWealthExceeds, so the two thresholds compare directly.
    // Applied to BOTH banking (auto-deposit) and stashing. The engine
    // converts as needed: the auto-deposit reroute passes this straight to
    // `dep <copper>` (the game auto-picks denominations), while
    // StashRoomManager decomposes held - keep into per-denomination
    // `hide N <coin>` commands (largest-first, exact because each MajorMUD
    // denomination divides the next). Default 0 = offload everything.
    public long KeepOnHandWealth { get; set; }

    // ----- Coin encumbrance gate + cascade ---------------------------
    // The "Cash + Items" tab exposes these; CashManager.CollectCoins gates coin
    // pickups against the bracket boundary they name.

    // Skip a coin pickup that would push the character into the Light
    // encumbrance bracket.
    public bool SkipCollectIfMakesLight { get; set; }

    // Skip a coin pickup that would push past Light → Medium.
    public bool SkipCollectIfMakesMedium { get; set; }

    // Skip a coin pickup that would push past Medium → Heavy.
    public bool SkipCollectIfMakesHeavy { get; set; }

    // Defer pickups until the room's combat finishes before sending gets.
    // Shared by CashManager and the AutoGetItemsManager item engine.
    public bool CollectAfterCombatFinished { get; set; }

    // When a Collect-flagged currency would push past an encumbrance gate, drop
    // just enough lower-value Collect-flagged held coin to make room. Never
    // sacrifices Ignore-flagged coin.
    public bool DropSmallerForLarger { get; set; }

    // ----- Item encumbrance gate -------------------------------------
    // Independent of the coin gate above: these cap the AutoGetItemsManager
    // ground-item pickups at the named bracket boundary. The hard capacity cap
    // (never grab an item that would exceed MaxWeight) is always on regardless
    // of these; the flags add the optional tighter bracket ceilings.

    // Skip a ground-item pickup that would push the character into Light.
    public bool SkipGetItemIfMakesLight { get; set; }

    // Skip a ground-item pickup that would push past Light → Medium.
    public bool SkipGetItemIfMakesMedium { get; set; }

    // Skip a ground-item pickup that would push past Medium → Heavy.
    public bool SkipGetItemIfMakesHeavy { get; set; }
}

// Per-currency pickup decision.
public enum CashPolicy
{
    // Don't touch — leave on the ground.
    Ignore,

    // Pick up via get all <coin>.
    Collect,

    // If we already hold any of this currency, drop it. Doesn't pick up new piles.
    Discard,
}
