using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestLootLedgerTests
{
    [Fact]
    public void ItemsPickedUpBetweenOpens_AreNotChestLoot()
    {
        // The first chest gives a moonstone. A fight in between adds 2 rusty daggers.
        // The second chest is diffed against the inventory read just before IT, so the
        // daggers never show as chest loot.
        var ledger = new ChestLootLedger();
        ledger.AddOpen(new[] { "oak chest", "oak chest" }, new[] { "oak chest", "moonstone" });
        ledger.AddOpen(new[] { "oak chest", "moonstone", "2 rusty dagger" },
                       new[] { "2 moonstone", "2 rusty dagger", "tiger-eye earrings" });

        var current = ledger.Current(new[] { "2 moonstone", "2 rusty dagger", "tiger-eye earrings" });

        Assert.Equal(new[] { ("moonstone", 2), ("tiger-eye earrings", 1) }, current);
    }

    [Fact]
    public void ItemsAlreadyCarried_OnlyTheOpensShareCounts()
    {
        // Already carrying 3 amber; the chest adds 1. Only that 1 is chest loot.
        var ledger = new ChestLootLedger();
        ledger.AddOpen(new[] { "3 piece of amber", "alder chest" }, new[] { "4 piece of amber" });

        Assert.Equal(new[] { ("piece of amber", 1) }, ledger.Current(new[] { "4 piece of amber" }));
    }

    [Fact]
    public void ConfirmedSales_TakeItemsBackOff()
    {
        var ledger = new ChestLootLedger();
        ledger.AddOpen(new[] { "oak chest" }, new[] { "3 moonstone", "ruby" });

        ledger.Remove("moonstone", 2);
        ledger.Remove("ruby", 1);

        Assert.Equal(new[] { ("moonstone", 1) }, ledger.Current(new[] { "moonstone" }));
    }

    [Fact]
    public void Current_IsCappedAtWhatsCarried()
    {
        // Two moonstones came out of the chest, one was given away unseen.
        var ledger = new ChestLootLedger();
        ledger.AddOpen(new[] { "oak chest" }, new[] { "2 moonstone" });

        Assert.Equal(new[] { ("moonstone", 1) }, ledger.Current(new[] { "moonstone" }));
        Assert.Empty(ledger.Current(new string[0]));
    }

    [Theory]
    [InlineData("chest", "oak chest")]
    [InlineData("oak chest", "oak chest")]
    [InlineData("OAK", "oak chest")]
    [InlineData("iron box", "iron box")]
    public void MatchContainer_ByNameOrWordPrefix(string typed, string expected)
        => Assert.Equal(expected, ChestOffloadPlanner.MatchContainer(typed, new[] { "oak chest", "iron box" }));

    [Theory]
    [InlineData("door")]
    [InlineData("north")]
    [InlineData("")]
    public void MatchContainer_NoContainerNamed_IsNull(string typed)
        => Assert.Null(ChestOffloadPlanner.MatchContainer(typed, new[] { "oak chest" }));

    [Fact]
    public void OutboundOpenObserver_AnnouncesTypedOpenTargets()
    {
        var observer = new OutboundOpenObserver();
        var seen = new List<string>();
        observer.OpenSent += seen.Add;

        observer.ObserveOutbound(Encoding.Latin1.GetBytes("open oak chest\r\n"));
        observer.ObserveOutbound(Encoding.Latin1.GetBytes("Open chest\r"));
        observer.ObserveOutbound(Encoding.Latin1.GetBytes("opening\r\n"));
        observer.ObserveOutbound(Encoding.Latin1.GetBytes("gossip open chest\r\n"));

        Assert.Equal(new[] { "oak chest", "chest" }, seen);
    }
}
