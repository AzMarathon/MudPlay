using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using Xunit;

namespace MudPlay.Tests;

// Pins the Stock spillover LOOK sweep: peek each exit, and report which neighbour
// rooms hold our still-missing items. It does not walk or grab — the caller
// (DeathRecoveryManager) drives the walk-collect off confirmed arrivals.
public sealed class DeathGroundSweepTests
{
    private static RoomKey Room(int r) => new(1, r);

    [Fact]
    public void Sweep_ReportsOnlyNeighboursHoldingOurItems()
    {
        var sent = new List<string>();
        IReadOnlyList<RoomKey>? result = null;
        var sweep = new DeathGroundSweep(sent.Add);
        var neighbours = new Dictionary<Direction, RoomKey>
        {
            [Direction.N] = Room(3),
            [Direction.E] = Room(5),
        };
        Assert.True(sweep.Begin(neighbours, new[] { "steel helm" }, h => result = h));

        Assert.Equal("look north", sent[0]);           // first look fires on Begin
        sweep.OnPeekedNotice(new[] { "a steel helm" }); // north holds our helm
        sweep.OnHeartbeat();                            // advance → look east
        Assert.Contains("look east", sent);
        sweep.OnPeekedNotice(new[] { "a rat" });        // east: nothing of ours
        sweep.OnHeartbeat();                            // looks done → complete

        Assert.NotNull(result);
        Assert.Equal(new[] { Room(3) }, result!);       // only north
        Assert.False(sweep.Active);
    }

    [Fact]
    public void Sweep_NoNeighbourHasOurItems_ReportsEmpty()
    {
        IReadOnlyList<RoomKey>? result = null;
        var sweep = new DeathGroundSweep(_ => { });
        Assert.True(sweep.Begin(
            new Dictionary<Direction, RoomKey> { [Direction.N] = Room(3) },
            new[] { "gold ring" }, h => result = h));

        sweep.OnPeekedNotice(new[] { "a rusty dagger" });   // not ours
        sweep.OnHeartbeat();                                 // looks done

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Sweep_NoExits_DoesNotStart()
    {
        var sweep = new DeathGroundSweep(_ => { });
        Assert.False(sweep.Begin(new Dictionary<Direction, RoomKey>(), new[] { "helm" }, _ => { }));
        Assert.False(sweep.Active);
    }
}
