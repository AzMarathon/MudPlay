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

    // ----- Minimum cash to keep on hand (BANKING) --------------------
    // The floor the character keeps after an auto-deposit, as an amount of a
    // chosen denomination: KeepOnHandWealth counts, KeepOnHandDenomination names
    // the coin, so "1 runic" keeps 1,000,000 copper on hand. The engine converts
    // to copper (amount × the denomination's copper unit) and deposits the excess
    // as `dep <copper>`. Applies to BANKING only — stashing is gated by
    // OnlyStashUpTo instead. Default 0 = deposit everything. (Legacy note:
    // KeepOnHandWealth used to be a raw copper value; with the default Copper
    // denomination it still reads back as the same copper amount, so old
    // profiles migrate cleanly.)
    public long KeepOnHandWealth { get; set; }
    public CoinDenomination KeepOnHandDenomination { get; set; } = CoinDenomination.Copper;

    // ----- Only stash coin up to (STASHING) --------------------------
    // When enabled, a stash offloads only coin denominations at or below
    // OnlyStashUpTo (e.g. "up to Gold" hides copper / silver / gold and keeps
    // platinum / runic in hand). Disabled ⇒ stash every denomination. Applies to
    // STASHING only — banking uses the keep-on-hand floor above.
    public bool OnlyStashUpToEnabled { get; set; }
    public CoinDenomination OnlyStashUpTo { get; set; } = CoinDenomination.Gold;

    // Stash while being dragged through a marked stash room as a party follower
    // (in a party, not leading). A follower's own loop / auto-lair is held by the
    // leader-drag gate, so the normal "passing through during automation" trigger
    // never fires for them; this opts a follower's pass-through back in. Default
    // off. Banking (which needs its own walk to the bank) is unaffected.
    public bool StashAsFollower { get; set; }

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

// A MajorMUD coin denomination, low to high. The copper-farthing unit of each
// (1 / 10 / 100 / 10,000 / 1,000,000) lives with the rest of the ratio ladder in
// Game.Inventory.CurrencyHoldings; declared low→high so the enum order matches
// value order (an "up to X" stash filter compares by it).
public enum CoinDenomination
{
    Copper,
    Silver,
    Gold,
    Platinum,
    Runic,
}
