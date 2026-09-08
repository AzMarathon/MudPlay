using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the demand-aware floor collector: it `get`s a still-outstanding
// NeedKind.PathItem the moment a floor survey reveals it, and only then — so a
// searched-up route counter is collected without depending on the Auto-Get engine.
public sealed class PathItemFloorCollectorTests
{
    private static PathItemFloorCollector Make(
        NeedsRegistry needs, HashSet<int> onFloor, List<string> sent) =>
        new(needs,
            isOnFloor: id => onFloor.Contains(id),
            itemName: id => $"item{id}",
            send: sent.Add);

    [Fact]
    public void Collects_OutstandingNeed_WhenOnFloor()
    {
        NeedsRegistry needs = new();
        needs.Post(NeedKind.PathItem, "42", "test");
        List<string> sent = new();
        var c = Make(needs, new HashSet<int> { 42 }, sent);

        c.CollectRevealed();

        Assert.Contains("get item42", sent);
    }

    [Fact]
    public void DoesNotCollect_WhenNotOnFloor()
    {
        NeedsRegistry needs = new();
        needs.Post(NeedKind.PathItem, "42", "test");
        List<string> sent = new();
        var c = Make(needs, new HashSet<int>(), sent);   // floor empty

        c.CollectRevealed();

        Assert.Empty(sent);
    }

    [Fact]
    public void DoesNotCollect_WhenNoOutstandingNeed()
    {
        NeedsRegistry needs = new();               // nothing posted
        List<string> sent = new();
        var c = Make(needs, new HashSet<int> { 42 }, sent);

        c.CollectRevealed();

        Assert.Empty(sent);
    }

    [Fact]
    public void DoesNotCollect_LightSourceNeed_OnlyPathItems()
    {
        NeedsRegistry needs = new();
        needs.Post(NeedKind.LightSource, "illu>=42", "test");
        List<string> sent = new();
        var c = Make(needs, new HashSet<int> { 42 }, sent);

        c.CollectRevealed();

        Assert.Empty(sent);   // a light-source need is not a path item
    }
}
