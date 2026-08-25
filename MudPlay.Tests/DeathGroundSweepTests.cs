using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using Xunit;

namespace MudPlay.Tests;

// Pins the Stock spillover sweep FSM: peek each exit, walk only to the neighbours
// holding our still-missing items, grab them, return, and complete. Driven by fed
// signals (peeked "You notice", "You took", walker arrivals, heartbeat pumps) so
// no live walker/terminal is needed.
public sealed class DeathGroundSweepTests
{
    private sealed class Harness
    {
        public List<string> Sent { get; } = new();
        public List<RoomKey> Walks { get; } = new();
        public DeathGroundSweep Sweep { get; }
        public bool Completed { get; private set; }

        public Harness()
            => Sweep = new DeathGroundSweep(Sent.Add, Walks.Add);

        public bool Begin(List<string> want, params (Direction Dir, RoomKey Key)[] neighbours)
        {
            var map = neighbours.ToDictionary(n => n.Dir, n => n.Key);
            return Sweep.Begin(new RoomKey(1, 1), map, want, () => Completed = true);
        }
    }

    private static RoomKey Room(int r) => new(1, r);

    [Fact]
    public void Sweep_LooksEveryExit_WalksOnlyToNeighboursWithOurItems_ThenReturns()
    {
        var h = new Harness();
        var want = new List<string> { "steel helm" };
        Assert.True(h.Begin(want, (Direction.N, Room(3)), (Direction.E, Room(5))));

        // First look fires on Begin; the helm is north, a rat is east.
        Assert.Equal("look north", h.Sent[0]);
        h.Sweep.OnPeekedNotice(new[] { "a steel helm" });
        h.Sweep.OnHeartbeat();                       // advance → look east
        Assert.Contains("look east", h.Sent);
        h.Sweep.OnPeekedNotice(new[] { "a rat" });
        h.Sweep.OnHeartbeat();                       // looks done → walk to the north neighbour

        Assert.Equal(Room(3), h.Walks[0]);           // only north (east had nothing of ours)
        h.Sweep.OnWalkerArrived(Room(3));            // arrive → grab
        Assert.Contains("get steel helm", h.Sent);
        h.Sweep.OnItemTaken("a steel helm");
        Assert.Empty(want);                          // decremented the shared list

        h.Sweep.OnHeartbeat();
        h.Sweep.OnHeartbeat();                       // collect settled → walk home
        Assert.Equal(Room(1), h.Walks[^1]);
        h.Sweep.OnWalkerArrived(Room(1));            // back at the death room → done

        Assert.True(h.Completed);
        Assert.False(h.Sweep.Active);
        Assert.DoesNotContain(h.Walks, k => k.Room == 5);   // never detoured east
    }

    [Fact]
    public void Sweep_NoNeighbourHasOurItems_CompletesWithoutWalking()
    {
        var h = new Harness();
        var want = new List<string> { "gold ring" };
        Assert.True(h.Begin(want, (Direction.N, Room(3))));

        h.Sweep.OnPeekedNotice(new[] { "a rusty dagger" });   // not ours
        h.Sweep.OnHeartbeat();                                // looks done → nothing to collect

        Assert.True(h.Completed);
        Assert.Empty(h.Walks);
        Assert.Single(want);            // still missing — caller marks Partial
    }

    [Fact]
    public void Sweep_NoExits_DoesNotStart()
    {
        var h = new Harness();
        Assert.False(h.Begin(new List<string> { "helm" }));   // no neighbours
        Assert.False(h.Sweep.Active);
    }
}
