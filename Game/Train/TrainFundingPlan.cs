using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Game.Train;

// Where a funding leg draws from. Stash is a room we believe holds coin we hid;
// Bank is a branch we hold a deposit at.
public enum TrainFundingSourceKind
{
    Stash,
    Bank,
}

// A place the run could draw money from, with what we BELIEVE is there. For a
// bank that belief is a parsed `bank` balance and is reliable; for a stash it is
// our own running tally of what we hid, and any player who searched the room
// since could have taken it — which is why an executed stash leg counts what it
// actually recovers rather than assuming this figure.
public readonly record struct TrainFundingSource(
    TrainFundingSourceKind Kind,
    RoomKey Room,
    string Name,
    long AvailableCopper);

// One stop on the funded route, in visit order.
public readonly record struct TrainFundingLeg(
    TrainFundingSourceKind Kind,
    RoomKey Room,
    string Name,
    long DrawCopper);

// The outcome of pricing a train against every source we can reach.
//
// Affordable means the plan's legs, plus what's already in the purse, add up to
// Cost on the numbers we had at plan time. It is not a promise: a stash can be
// robbed and a bank balance can be stale, so the executor re-checks after each
// leg and re-plans from what it actually holds.
public readonly record struct TrainFundingPlan(
    bool Affordable,
    long Cost,
    long OnHandCopper,
    IReadOnlyList<TrainFundingLeg> Legs,
    long ShortfallCopper,
    long SpeculativeCopper)
{
    // The plan only adds up if the stashes still hold what we think they do.
    // Coin in the purse and coin in a bank are certain; coin left hidden in a room
    // is not, because any player who searched it since could have walked off with
    // it. A speculative plan is still worth walking — the stash is usually intact,
    // and the alternative is not training at all — but it must be logged as a
    // gamble, and the run has to re-plan in place when a leg disappoints rather
    // than assuming the rest of the arithmetic still holds.
    public bool DependsOnStash => Affordable && SpeculativeCopper > 0;
}
