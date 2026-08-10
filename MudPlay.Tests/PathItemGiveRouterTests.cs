using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins PathItemGiveRouter's detour FSM: on a path-item need an NPC / room hands
// over for free, walk to the fewest-added-steps giver, issue its command
// verbatim, and resume once the item lands — with graceful fail-outs (feature
// off, engine walk, unreachable / mistimed give, user takeover).
public sealed class PathItemGiveRouterTests
{
    private static readonly RoomKey Cur = new(1, 100);
    private static readonly RoomKey Dest = new(1, 200);
    private static readonly RoomKey GiverA = new(1, 150);
    private static readonly RoomKey GiverB = new(1, 160);

    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    private static Need PathNeed(int id, int qty = 1)
        => new(NeedKind.PathItem, id.ToString(), "test", DateTimeOffset.Now, qty);

    private sealed class Harness
    {
        public readonly Dictionary<int, List<GiveSource>> Givers = new();
        public readonly Dictionary<(RoomKey From, RoomKey To), int> Dist = new();
        public readonly Dictionary<int, int> Carried = new();
        public readonly Dictionary<int, string> Names = new() { [42] = "bloodstone orb" };
        public RoomKey? Current = Cur;
        public RoomKey? WalkDest = Dest;
        public bool Enabled = true;
        public bool EngineWalk;
        public readonly List<RoomKey> Walks = new();

        public void Carry(int id, int n = 1) => Carried[id] = n;

        public PathItemGiveRouter Build()
        {
            var r = new PathItemGiveRouter(
                giveSourcesForItem: id => Givers.TryGetValue(id, out List<GiveSource>? g)
                    ? g
                    : (IReadOnlyList<GiveSource>)Array.Empty<GiveSource>(),
                currentRoom: () => Current,
                walkDestination: () => WalkDest,
                distanceBetween: (a, b) => Dist.TryGetValue((a, b), out int d) ? d : null,
                carriedCount: id => Carried.TryGetValue(id, out int c) ? c : 0,
                itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
                isEnabled: _ => Enabled,
                engineWalkActive: () => EngineWalk,
                walkTo: Walks.Add,
                post: a => a(),                       // synchronous in tests
                log: null,
                giveTimeout: TimeSpan.FromHours(1));   // real timer never fires mid-test
            r.SetWireSender(_sent.Add);
            return r;
        }

        private readonly List<byte[]> _sent = new();
        public IReadOnlyList<byte[]> Sent => _sent;

        // One NPC giver (Gnome Commander) at GiverA, three steps out, four on to
        // dest, asked "ask commander orb" (the ask noun is the name's last word).
        public Harness WithNpcGiver()
        {
            Givers[42] = new List<GiveSource> { new(GiverA, "ask commander orb", "Gnome Commander") };
            Dist[(Cur, GiverA)] = 3;
            Dist[(GiverA, Dest)] = 4;
            return this;
        }
    }

    private static WalkEvent Finished(RoomKey dest) => new(WalkEventKind.Finished, "reached", dest);
    private static WalkEvent Failed(RoomKey dest) => new(WalkEventKind.Failed, "no path", dest);
    private static WalkEvent Stopped() => new(WalkEventKind.Stopped, "user walk", null);

    [Fact]
    public void OnNeedPosted_GiverExists_DetoursToGiver()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.True(r.DetourActive);
        Assert.Equal(GiverA, Assert.Single(h.Walks));
    }

    [Fact]
    public void ArrivingAtGiver_IssuesCommandVerbatim()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(Finished(GiverA));

        Assert.Equal("ask commander orb", Decode(Assert.Single(h.Sent)));
    }

    [Fact]
    public void RoomGiver_IssuesBareKeyword()
    {
        var h = new Harness();
        h.Givers[42] = new List<GiveSource> { new(GiverA, "insert fang", "Dragon Statue") };
        h.Dist[(Cur, GiverA)] = 2;
        h.Dist[(GiverA, Dest)] = 2;
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverA));

        Assert.Equal("insert fang", Decode(Assert.Single(h.Sent)));
    }

    [Fact]
    public void ItemLandsAfterGive_ResumesToDestination()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverA));

        h.Carry(42);
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);   // last walk is the resume to dest
    }

    [Fact]
    public void OnNeedPosted_FeatureOff_NoDetour()
    {
        var h = new Harness().WithNpcGiver();
        h.Enabled = false;
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_EngineWalkActive_NoDetour()
    {
        var h = new Harness().WithNpcGiver();
        h.EngineWalk = true;
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_ItemAlreadyCarried_NoDetour()
    {
        var h = new Harness().WithNpcGiver();
        h.Carry(42);
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
    }

    [Fact]
    public void OnNeedPosted_NoGiver_NoDetour()
    {
        var h = new Harness();           // no Givers entry
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void OnNeedPosted_TwoGivers_PicksFewestAddedSteps()
    {
        var h = new Harness();
        h.Givers[42] = new List<GiveSource>
        {
            new(GiverA, "ask a orb", "A"),   // 3 + 4 = 7
            new(GiverB, "ask b orb", "B"),   // 1 + 2 = 3 (nearer overall)
        };
        h.Dist[(Cur, GiverA)] = 3;
        h.Dist[(GiverA, Dest)] = 4;
        h.Dist[(Cur, GiverB)] = 1;
        h.Dist[(GiverB, Dest)] = 2;
        PathItemGiveRouter r = h.Build();

        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverB));

        Assert.Equal(GiverB, h.Walks[0]);
        Assert.Equal("ask b orb", Decode(Assert.Single(h.Sent)));
    }

    [Fact]
    public void GiveTimeout_ItemNeverLands_ResumesToDestination()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverA));

        r.OnGiveTimeout();               // give never landed

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    [Fact]
    public void WalkToGiverFails_ResumesToDestination()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(Failed(GiverA));

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
        Assert.Empty(h.Sent);            // never reached the giver to ask
    }

    [Fact]
    public void ItemFoundWhileWalkingToGiver_AbortsAndResumes()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        h.Carry(42);                     // search / party hand-off turned it up en route
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void UserRedirectsDuringDetour_AbandonsQuietly()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(Stopped());        // user / another engine took over

        Assert.False(r.DetourActive);
        Assert.Single(h.Walks);          // only the original detour walk; no resume
    }
}
