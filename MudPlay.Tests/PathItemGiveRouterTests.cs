using System;
using System.Collections.Generic;
using System.Linq;
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

        // The ask, then an inventory re-read — the hand-over line is per-giver
        // flavor text, so `i` is the reliable test of whether the give landed.
        Assert.Equal(new[] { "ask commander orb", "i" }, h.Sent.Select(Decode).ToArray());
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

        Assert.Equal(new[] { "insert fang", "i" }, h.Sent.Select(Decode).ToArray());
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
        Assert.Equal(new[] { "ask b orb", "i" }, h.Sent.Select(Decode).ToArray());
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
    // ----- inventory re-read after the ask ---------------------------
    //
    // Report paradigm-20260911-103025: the commander DID hand the orb over, but the
    // only evidence was per-give flavor text ("The gnome commander gives you the
    // heavy bloodstone orb.") — wording no parser owns and none should try to. With
    // nothing re-reading the pack, the need never resolved, the give window expired,
    // and the walk resumed and failed for want of an item already in inventory.

    [Fact]
    public void GiveCommand_IsFollowedByAnInventoryReRead()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));

        r.OnWalkEvent(Finished(GiverA));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("i", Decode(h.Sent[1]));   // after the ask, never before it
    }

    // The re-read's whole purpose: the reply lands, the item is seen, the walk
    // resumes — without anyone having parsed the hand-over sentence.
    [Fact]
    public void ItemSeenByTheReRead_ResumesWithoutParsingTheGiveLine()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverA));

        // What the `i` reply does: the inventory parse lands and fires Changed.
        h.Carry(42);
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    // A give that genuinely didn't land still falls through to the other
    // fulfillers — the re-read must not make a failure look like a success.
    [Fact]
    public void ReReadShowingNothing_StillTimesOutAndResumes()
    {
        var h = new Harness().WithNpcGiver();
        PathItemGiveRouter r = h.Build();
        r.OnNeedPosted(PathNeed(42));
        r.OnWalkEvent(Finished(GiverA));

        r.OnInventoryChanged();   // reply parsed, item absent
        Assert.True(r.DetourActive);

        r.OnGiveTimeout();
        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

}
