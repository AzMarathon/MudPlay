using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Tokens;
using Xunit;

namespace MudPlay.Tests;

// Pins TokenRouteEvaluator.Best: it picks the held token whose landing→destination
// walk saves the most rooms over the overland walk, honours the min-rooms-shorter
// floor, skips a depleted token (0 charges) but keeps a not-yet-looked one (null),
// skips a token whose landing can't reach the destination, and — when obtaining an
// acquirable gate item (buy a raft, key a door) reaches the destination in as few
// steps as the token's onward walk — steps aside so the caller defers to the item-gate
// picker.
public sealed class TokenRouteEvaluatorTests
{
    private static TokenTeleportInfo Token(string place, int map, int room, int minLevel = 1, long cost = 0)
        => new(0, place, new RoomKey(map, room), cost, minLevel);

    [Fact]
    public void Best_PicksTokenSavingTheMostRooms()
    {
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenTeleportInfo b = Token("Beta", 1, 200);
        var steps = new Dictionary<RoomKey, int?> { [a.Destination] = 5, [b.Destination] = 2 };

        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 20,
            obtainableSteps: null,
            heldTokens: new[] { (a, (int?)3), (b, (int?)3) },
            stepsFromLanding: k => steps[k],
            minRoomsShorter: 3);

        Assert.NotNull(best);
        Assert.Equal("Beta", best!.Value.Place);   // saves 18 vs Alpha's 15
        Assert.Equal(18, best.Value.RoomsSaved);
        Assert.Equal(2, best.Value.TokenWalkSteps);
        Assert.Equal(20, best.Value.OverlandSteps);
    }

    [Fact]
    public void Best_ReturnsNullWhenNoneBeatsThreshold()
    {
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 10,
            obtainableSteps: null,
            heldTokens: new[] { (a, (int?)3) },
            stepsFromLanding: _ => 8,   // saves only 2
            minRoomsShorter: 3);
        Assert.Null(best);
    }

    [Fact]
    public void Best_SkipsDepletedToken()
    {
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 20,
            obtainableSteps: null,
            heldTokens: new[] { (a, (int?)0) },
            stepsFromLanding: _ => 2,
            minRoomsShorter: 3);
        Assert.Null(best);
    }

    [Fact]
    public void Best_KeepsNotYetLookedToken()
    {
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 20,
            obtainableSteps: null,
            heldTokens: new[] { (a, (int?)null) },
            stepsFromLanding: _ => 2,
            minRoomsShorter: 3);
        Assert.NotNull(best);
        Assert.Null(best!.Value.Charges);
    }

    [Fact]
    public void Best_SkipsUnreachableLanding()
    {
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 20,
            obtainableSteps: null,
            heldTokens: new[] { (a, (int?)3) },
            stepsFromLanding: _ => null,   // landing can't reach the destination
            minRoomsShorter: 3);
        Assert.Null(best);
    }

    [Fact]
    public void Best_DefersToken_WhenBuyAndCrossReachesInFewerSteps()
    {
        // Report paradigm-20260917-233549: standing at the Pier, overland (no raft) is 456,
        // but buying a raft and sailing is 114; a token to the Lost City lands 296 from the
        // turtle. 296 >= 114, so the token steps aside for buy-and-cross rather than
        // teleporting away (the caller then defers to the item-gate picker).
        TokenTeleportInfo lostCity = Token("the Lost City", 16, 426);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 456,
            obtainableSteps: 114,
            heldTokens: new[] { (lostCity, (int?)5) },
            stepsFromLanding: _ => 296,
            minRoomsShorter: 1);
        Assert.Null(best);
    }

    [Fact]
    public void Best_KeepsToken_WhenGenuinelyShorterThanBuyAndCross()
    {
        // The obtainable buy-and-cross route is 114, but this token lands just 2 rooms from
        // the destination — genuinely shorter than buying and crossing, so it's still offered.
        TokenTeleportInfo a = Token("Alpha", 1, 100);
        TokenRouteCandidate? best = TokenRouteEvaluator.Best(
            overlandSteps: 456,
            obtainableSteps: 114,
            heldTokens: new[] { (a, (int?)3) },
            stepsFromLanding: _ => 2,
            minRoomsShorter: 3);
        Assert.NotNull(best);
        Assert.Equal(2, best!.Value.TokenWalkSteps);
    }
}
