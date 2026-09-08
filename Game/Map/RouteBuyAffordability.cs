using System.Collections.Generic;

namespace MudPlay.Game.Map;

// Where the money for a route-gate / hazard-counter purchase actually is, once
// the picker has checked the purse, the character's own bank deposits (via the
// `bank` listing) and the party's on-hand cash (via @wealth). Drives both the
// card wording and the commit path:
//   Cash / ConfiguredBank  → the auto obtain-pipeline can source it (it buys with
//                            cash, withdrawing from the configured bank if short).
//   ElsewhereBank / Party  → the leader can't pay from what the pipeline reaches,
//                            but the money EXISTS (on deposit at another bank, or
//                            carried by the party) — so the commit walks to the
//                            shop and pauses for the user to sort it out.
//   Unaffordable           → nobody has it; the card warns and (for a survivable
//                            hazard) still offers to cross unprotected.
public enum RouteBuySource
{
    Cash,
    ConfiguredBank,
    ElsewhereBank,
    PartyPooled,
    Unaffordable,
}

// The picker's affordability verdict for one buy: the source that covers it, the
// total cost, how much must come from beyond the purse (Shortfall), the bank the
// money sits in when relevant (BankName), and a human note for the card tail.
public readonly record struct RouteBuyAffordability(
    RouteBuySource Source,
    long Cost,
    long Shortfall,
    string? BankName,
    string Note);

// The full pick-time economy read for a route that needs buying something: the
// affordability verdict, the nearest shop the buy would happen at (so a
// not-auto-payable commit can walk there and pause), and a party member who
// already holds a needed item (so the card can offer "provision from X" instead
// of buying). PartyItemHolder is null when no member has it / not in a party.
public readonly record struct RouteBuyEconomy(
    RouteBuyAffordability Affordability,
    RoomKey? ShopRoom,
    string? PartyItemHolder)
{
    // The auto obtain-pipeline can pay for it (cash, or a withdraw from the
    // configured bank) — so the existing "obtain then cross" commit is fine.
    public bool AutoPayable =>
        Affordability.Source is RouteBuySource.Cash or RouteBuySource.ConfiguredBank;
}

// Pure classifier: given a cost and the money the character/party can reach,
// decide which source covers it. Kept free of AppServices/game-data so it's unit
// testable — the caller supplies the copper figures (cash on hand, the configured
// bank's deposit, other reachable used banks' deposits nearest-first, and the
// party's summed on-hand cash excluding self). All amounts are copper farthings.
public static class RouteBuyAffordabilityCalculator
{
    public static RouteBuyAffordability Classify(
        long cost,
        long ownCash,
        long configuredBankDeposit,
        IReadOnlyList<(string Name, long Deposit)> otherReachableBanks,
        long partyOnHand)
    {
        ArgumentNullException.ThrowIfNull(otherReachableBanks);

        if (cost <= 0 || ownCash >= cost)
            return new(RouteBuySource.Cash, cost, 0, null, "buy with cash on hand");

        long shortfall = cost - ownCash;

        // The auto obtain-pipeline withdraws from the configured bank, so a
        // configured-bank deposit that covers the shortfall stays on the auto path.
        if (configuredBankDeposit >= shortfall)
            return new(RouteBuySource.ConfiguredBank, cost, shortfall, null,
                $"withdraw ~{shortfall:N0} copper at your bank first");

        // The money's on deposit at a bank the pipeline won't auto-visit — name the
        // nearest one whose deposit covers it (the caller passes them nearest-first),
        // else the nearest one at all. The leader must go there themselves, so this
        // routes to the walk-to-shop-and-pause commit.
        long otherBankTotal = 0;
        string? coveringBank = null;
        foreach ((string name, long deposit) in otherReachableBanks)
        {
            otherBankTotal += deposit;
            if (coveringBank is null && configuredBankDeposit + deposit >= shortfall)
                coveringBank = name;
        }
        if (configuredBankDeposit + otherBankTotal >= shortfall)
        {
            string where = coveringBank ?? (otherReachableBanks.Count > 0 ? otherReachableBanks[0].Name : "your bank");
            return new(RouteBuySource.ElsewhereBank, cost, shortfall, where,
                $"you're short ~{shortfall:N0} copper — it's on deposit at {where}");
        }

        // Own money (cash + every reachable deposit) still short, but the party's
        // on-hand cash closes the gap → walk there and provision by hand.
        if (ownCash + configuredBankDeposit + otherBankTotal + partyOnHand >= cost)
            return new(RouteBuySource.PartyPooled, cost, shortfall, coveringBank,
                $"you're short ~{shortfall:N0} copper — the party has it on hand; walk there and provision");

        return new(RouteBuySource.Unaffordable, cost, shortfall, null,
            $"short ~{shortfall:N0} copper — nobody has it on hand or on deposit");
    }
}
