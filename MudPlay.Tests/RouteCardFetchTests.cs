using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The lifetime of what a route card's pick ordered. A trade hands an item of the
// user's over, so the rule these pin is that it is in force for the one walk the
// card started and for nothing before, beside or after it.
public sealed class RouteCardFetchTests
{
    private const int Key = 808, Brooch = 811, Raft = 5, Rope = 6;

    private static readonly RoomKey Dest = new(8, 557);
    private static readonly RoomKey Giver = new(8, 486);
    private static readonly RoomKey Elsewhere = new(1, 100);

    private static WalkEvent Started(RoomKey to) => new(WalkEventKind.Started, "", to);
    private static WalkEvent Finished(RoomKey at) => new(WalkEventKind.Finished, "", at);
    private static WalkEvent Failed(RoomKey to) => new(WalkEventKind.Failed, "no path", to);
    private static WalkEvent Stopped(RoomKey? to = null) => new(WalkEventKind.Stopped, "stopped", to);

    // A Gated pick as the route prompt makes it: the trades, the hazard counters,
    // the gate items, then the walk.
    private static RouteCardFetch Picked(bool startWalk = true)
    {
        var fetch = new RouteCardFetch();
        fetch.AgreeTrades(Dest, new[] { (Key, Brooch) });
        fetch.Fetch(new[] { Raft });
        fetch.Fetch(new[] { Key });
        if (startWalk) fetch.OnWalkEvent(Started(Dest));
        return fetch;
    }

    [Fact]
    public void Trade_IsInForceOnlyOnceItsWalkHasStarted()
    {
        RouteCardFetch fetch = Picked(startWalk: false);
        Assert.Null(fetch.AgreedTradeFor(Key));

        fetch.OnWalkEvent(Started(Dest));

        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));
        Assert.Null(fetch.AgreedTradeFor(Raft));
    }

    // A pick's two fetch calls add up: the second used to replace the first, and
    // the walk crossed its hazard without the counter.
    [Fact]
    public void WithinOnePick_FetchCallsAddUp()
    {
        RouteCardFetch fetch = Picked();

        Assert.True(fetch.Fetches(Raft));
        Assert.True(fetch.Fetches(Key));
    }

    // Any other order replaces what was held, trades included.
    [Fact]
    public void FetchOutsideAPick_StartsAFreshOrderWithNoTrades()
    {
        RouteCardFetch fetch = Picked();

        fetch.Fetch(new[] { Rope });
        fetch.OnWalkEvent(Started(Dest));

        Assert.True(fetch.Fetches(Rope));
        Assert.False(fetch.Fetches(Raft));
        Assert.False(fetch.Fetches(Key));
        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // Picking the card while another walk is under way stops that walk first. Its
    // Stopped is not this order's: wiping the order there left the new walk with
    // nothing arranged.
    [Fact]
    public void PickDuringAnotherWalk_SurvivesTheOldWalksStop()
    {
        RouteCardFetch fetch = Picked(startWalk: false);

        fetch.OnWalkEvent(Stopped(Elsewhere));
        fetch.OnWalkEvent(Stopped(Elsewhere));
        Assert.True(fetch.Fetches(Key));
        Assert.Null(fetch.AgreedTradeFor(Key));

        fetch.OnWalkEvent(Started(Dest));
        Assert.True(fetch.Fetches(Raft));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));
    }

    // The give failed, the door happened to stand open, the walk arrived. Nothing
    // of the order is left for whatever walks next.
    [Fact]
    public void WalkArrives_OrderEnds_AndALaterWalkReadsNothing()
    {
        RouteCardFetch fetch = Picked();
        var ended = new List<string>();
        fetch.Ended += ended.Add;

        fetch.OnWalkEvent(Finished(Dest));

        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.False(fetch.HasItems);
        Assert.Single(ended);

        // A trainer trip, a stash transfer, a loop's approach: across the same door
        // or to the same room.
        fetch.OnWalkEvent(Started(Elsewhere));
        Assert.Null(fetch.AgreedTradeFor(Key));
        fetch.OnWalkEvent(Finished(Elsewhere));
        fetch.OnWalkEvent(Started(Dest));
        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.False(fetch.Fetches(Key));
    }

    [Theory]
    [InlineData(WalkEventKind.Stopped)]
    [InlineData(WalkEventKind.Failed)]
    public void WalkStoppedOrFailed_OrderEnds(WalkEventKind how)
    {
        RouteCardFetch fetch = Picked();

        fetch.OnWalkEvent(new WalkEvent(how, "", Dest));
        fetch.OnWalkEvent(Started(Dest));

        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.False(fetch.HasItems);
    }

    // The detour to the trader and the walk back are legs of the same walk. A leg
    // that waits for moves to settle announces its start twice.
    [Fact]
    public void OwnFetchDetour_KeepsTheTradeInForce()
    {
        RouteCardFetch fetch = Picked();

        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Giver));
        fetch.OnWalkEvent(Started(Giver));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));

        fetch.OnWalkEvent(Finished(Giver));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));

        fetch.NoteOwnLeg(Dest);
        fetch.OnWalkEvent(Started(Dest));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));
        Assert.True(fetch.Fetches(Raft));
    }

    // The walker standing idle at the giver, waiting for the hand-over, when some
    // other walk takes it: no Stopped is raised, only the newcomer's start.
    [Fact]
    public void AnotherWalkStartsWithoutAStop_TradeIsGone()
    {
        RouteCardFetch fetch = Picked();
        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Giver));
        fetch.OnWalkEvent(Finished(Giver));

        fetch.OnWalkEvent(Started(Elsewhere));

        Assert.Null(fetch.AgreedTradeFor(Key));
        // Back at the old destination afterwards changes nothing.
        fetch.OnWalkEvent(Started(Dest));
        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // A leg announced for one room doesn't excuse a walk to another.
    [Fact]
    public void AnnouncedLeg_CoversOnlyItsOwnTarget()
    {
        RouteCardFetch fetch = Picked();

        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Elsewhere));

        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // The card was for one destination. If the walk that sets off next goes
    // somewhere else, the trade was never for it.
    [Fact]
    public void WalkToAnotherDestinationStartsInstead_TradeNeverComesIntoForce()
    {
        RouteCardFetch fetch = Picked(startWalk: false);

        fetch.OnWalkEvent(Started(Elsewhere));

        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.Empty(fetch.Trades);
    }

    // The committed walk never starts: no path, or already standing there.
    [Theory]
    [InlineData(WalkEventKind.Failed)]
    [InlineData(WalkEventKind.Finished)]
    public void WalkNeverStarts_OrderEnds(WalkEventKind how)
    {
        RouteCardFetch fetch = Picked(startWalk: false);

        fetch.OnWalkEvent(new WalkEvent(how, "", Dest));
        fetch.OnWalkEvent(Started(Dest));

        Assert.False(fetch.HasItems);
        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // Death, a dropped connection, a profile change, Stop and an engine reset can
    // all come with the walker idle, where no walk event tells of them.
    [Fact]
    public void End_WhileWaitingAtTheGiver_DropsEverything_AndSaysWhy()
    {
        RouteCardFetch fetch = Picked();
        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Giver));
        fetch.OnWalkEvent(Finished(Giver));
        var ended = new List<string>();
        fetch.Ended += ended.Add;

        fetch.End("the player died");

        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.False(fetch.HasItems);
        Assert.Equal(new[] { "the player died" }, ended);

        // The walk the detour would have resumed finds nothing.
        fetch.NoteOwnLeg(Dest);
        fetch.OnWalkEvent(Started(Dest));
        Assert.Null(fetch.AgreedTradeFor(Key));

        fetch.End("again");
        Assert.Single(ended);
    }

    [Fact]
    public void End_BeforeTheWalkStarts_NothingIsAdoptedLater()
    {
        RouteCardFetch fetch = Picked(startWalk: false);

        fetch.End("stopped");
        fetch.OnWalkEvent(Started(Dest));

        Assert.False(fetch.HasItems);
        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // A trade is for a key the walk is fetching. Once the key is in hand there is
    // nothing left to trade for.
    [Fact]
    public void KeyCovered_ItsTradeIsNoLongerInForce()
    {
        RouteCardFetch fetch = Picked();

        fetch.DropCovered(id => id == Key);

        Assert.Null(fetch.AgreedTradeFor(Key));
        Assert.True(fetch.Fetches(Raft));
    }

    // A second card picked while the first card's walk runs: the first order is
    // gone at once, and its trade doesn't come back when the new walk starts.
    [Fact]
    public void ANewPick_ReplacesTheOrderOfTheWalkItSupersedes()
    {
        RouteCardFetch fetch = Picked();

        fetch.AgreeTrades(Elsewhere, System.Array.Empty<(int, int)>());
        fetch.Fetch(new[] { Rope });
        Assert.Null(fetch.AgreedTradeFor(Key));
        fetch.OnWalkEvent(Stopped(Dest));
        fetch.OnWalkEvent(Started(Elsewhere));

        Assert.True(fetch.Fetches(Rope));
        Assert.False(fetch.Fetches(Key));
        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // Steps, pauses and retries are not the walk ending.
    [Theory]
    [InlineData(WalkEventKind.StepCompleted)]
    [InlineData(WalkEventKind.Paused)]
    [InlineData(WalkEventKind.Resumed)]
    [InlineData(WalkEventKind.Retrying)]
    public void EventsWithinAWalk_ChangeNothing(WalkEventKind kind)
    {
        RouteCardFetch fetch = Picked();

        fetch.OnWalkEvent(new WalkEvent(kind, "", Dest));

        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));
        Assert.True(fetch.Fetches(Raft));
    }

    // Idle at the giver, and the walk that takes the walker happens to go to the
    // very room the card's walk was for. It is still not the card's walk.
    [Fact]
    public void AnotherWalkToTheSameDestination_FromIdle_IsNotTheCardsWalk()
    {
        RouteCardFetch fetch = Picked();
        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Giver));
        fetch.OnWalkEvent(Finished(Giver));

        fetch.OnWalkEvent(Started(Dest));

        Assert.Null(fetch.AgreedTradeFor(Key));
    }

    // Mid-walk, a start to where the walk is already heading is its own re-plan.
    [Fact]
    public void ReplanOfTheWalkOrOfALeg_KeepsTheTrade()
    {
        RouteCardFetch fetch = Picked();
        fetch.OnWalkEvent(Started(Dest));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));

        fetch.NoteOwnLeg(Giver);
        fetch.OnWalkEvent(Started(Giver));
        fetch.OnWalkEvent(Started(Giver));
        Assert.Equal(Brooch, fetch.AgreedTradeFor(Key));

        // But not a start back to the destination nobody announced.
        fetch.OnWalkEvent(Started(Dest));
        Assert.Null(fetch.AgreedTradeFor(Key));
    }
}
