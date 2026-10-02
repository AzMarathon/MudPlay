using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The shop leg of a train trip as a state machine. What matters most is that no
// reply — an empty shelf, a refused buy, a read the game answers with a line we
// don't know — can leave the run standing in a shop.
public sealed class ShopSpellErrandTests
{
    private static readonly RoomKey Trainer = new(1, 10);
    private static readonly RoomKey Loop = new(1, 1);
    private static readonly RoomKey ShopA = new(1, 20);
    private static readonly RoomKey ShopB = new(1, 40);

    private static readonly ShopSpellPurchase Smite = new(2, "smite", 3, 102, "scroll of smite", 400);
    private static readonly ShopSpellPurchase Flash = new(3, "flash", 7, 103, "scroll of flash", 900);
    private static readonly ShopSpellPurchase Bliz = new(4, "blizzard", 9, 104, "scroll of blizzard", 3400);

    private sealed class Harness
    {
        public RoomKey Room = Trainer;
        public Dictionary<RoomKey, List<ShopSpellPurchase>> Shops = new();
        public HashSet<int> Obtained = new();
        public Dictionary<int, int> Pack = new();
        public List<string> Sent = new();
        public List<RoomKey> Walked = new();
        public List<long> Reserved = new();
        public bool WalkSucceeds = true;
        public ShopSpellResult? Result;
        public readonly ShopSpellErrand Errand;
        private readonly List<Action> _timers = new();

        public Harness()
        {
            Errand = new ShopSpellErrand(
                currentRoom: () => Room,
                plan: (from, returnTo, level, visited, gaveUp) =>
                {
                    List<ShopSpellStop> stops = new();
                    foreach ((RoomKey room, List<ShopSpellPurchase> scrolls) in Shops.OrderBy(s => Math.Abs(s.Key.Room - from.Room)))
                    {
                        if (visited.Contains(room)) continue;
                        List<ShopSpellPurchase> want = scrolls
                            .Where(s => !Obtained.Contains(s.SpellNumber) && !gaveUp.Contains(s.SpellNumber)).ToList();
                        if (want.Count > 0) stops.Add(new(room, $"shop {room.Room}", want));
                    }
                    return new(stops, stops.Sum(s => s.Purchases.Sum(p => p.PriceCopper)),
                        Array.Empty<string>(), Array.Empty<string>());
                },
                carriedScrolls: _ => Shops.Values.SelectMany(s => s).Concat(new[] { Bliz })
                    .Where(s => Pack.GetValueOrDefault(s.ItemNumber) > 0 && !Obtained.Contains(s.SpellNumber))
                    .Distinct().ToList(),
                carriedCount: item => Pack.GetValueOrDefault(item),
                isObtained: Obtained.Contains,
                walkTo: room => { Walked.Add(room); return WalkSucceeds; },
                send: Sent.Add,
                armTimer: (_, action) => _timers.Add(action),
                reserve: Reserved.Add);
            Errand.Finished += r => Result = r;
        }

        public void FireTimers()
        {
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action a in due) a();
        }

        public void Arrive(RoomKey room)
        {
            Room = room;
            Errand.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "", room));
        }

        public void List(params (string Name, int Quantity)[] rows) =>
            Errand.OnShopListed(rows.Select(r => new ShopListParser.StockRow(r.Name, r.Quantity, "1 gold crown")).ToArray());

        public void Bought(ShopSpellPurchase scroll)
        {
            Pack[scroll.ItemNumber] = 1;
            Errand.OnInventoryChanged();
        }

        public void Learned(ShopSpellPurchase scroll)
        {
            Pack.Remove(scroll.ItemNumber);
            Obtained.Add(scroll.SpellNumber);
            Errand.OnSpellbookChanged();
        }
    }

    [Fact]
    public void NothingToBuyAndNothingCarriedStartsNoTrip()
    {
        Harness h = new();

        Assert.False(h.Errand.Begin(5, Loop));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ReadsTheSpellListThenWalksListsBuysAndReads()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite, Flash };

        Assert.True(h.Errand.Begin(7, Loop));
        Assert.Equal(new[] { "sp" }, h.Sent);
        h.FireTimers();
        Assert.Equal(new[] { ShopA }, h.Walked);

        h.Arrive(ShopA);
        Assert.Equal("list", h.Sent[^1]);
        h.List(("scroll of smite", 3), ("scroll of flash", 1));
        Assert.Equal("buy scroll of smite", h.Sent[^1]);
        h.Bought(Smite);
        Assert.Equal("read scroll of smite", h.Sent[^1]);
        h.Learned(Smite);
        Assert.Equal("buy scroll of flash", h.Sent[^1]);
        h.Bought(Flash);
        h.Learned(Flash);

        Assert.Equal(new[] { "smite", "flash" }, h.Result!.Value.Learned);
        Assert.False(h.Result.Value.Aborted);
        Assert.False(h.Errand.IsBusy);
        Assert.Equal(0, h.Reserved[^1]);
    }

    [Fact]
    public void AnOutOfStockScrollIsNotBoughtAndTheTripMovesOn()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Shops[ShopB] = new() { Flash };
        h.Errand.Begin(7, Loop);
        h.FireTimers();

        h.Arrive(ShopA);
        h.List(("scroll of smite", 0), ("torch", 250));

        Assert.DoesNotContain("buy scroll of smite", h.Sent);
        Assert.Equal(new[] { ShopA, ShopB }, h.Walked);
    }

    [Fact]
    public void ABuyThatNeverLandsTimesOutInsteadOfStalling()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Errand.Begin(7, Loop);
        h.FireTimers();
        h.Arrive(ShopA);
        h.List(("scroll of smite", 1));

        h.FireTimers();

        Assert.NotNull(h.Result);
        Assert.Empty(h.Result!.Value.Learned);
    }

    [Fact]
    public void ARefusedBuySkipsThatScrollOnly()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Bliz, Smite };
        h.Errand.Begin(9, Loop);
        h.FireTimers();
        h.Arrive(ShopA);
        h.List(("scroll of blizzard", 1), ("scroll of smite", 1));

        h.Errand.OnBuyRefused("scroll of blizzard");

        Assert.Equal("buy scroll of smite", h.Sent[^1]);
    }

    [Fact]
    public void AnUnansweredReadGivesUpOnThatSpellForTheTrip()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Shops[ShopB] = new() { Smite };
        h.Errand.Begin(7, Loop);
        h.FireTimers();
        h.Arrive(ShopA);
        h.List(("scroll of smite", 1));
        h.Bought(Smite);

        h.FireTimers();   // the read window closes with no spell learned

        Assert.NotNull(h.Result);
        Assert.Equal(new[] { ShopA }, h.Walked);
    }

    [Fact]
    public void AScrollTheGameSaysIsAlreadyKnownIsNamedForTheSpellbook()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Errand.Begin(7, Loop);
        h.FireTimers();
        Assert.Null(h.Errand.OnScrollAlreadyKnown());
        h.Arrive(ShopA);
        h.List(("scroll of smite", 1));
        h.Bought(Smite);

        Assert.Equal("smite", h.Errand.OnScrollAlreadyKnown());
        Assert.NotNull(h.Result);
    }

    [Fact]
    public void AScrollAlreadyInThePackIsReadBeforeAnyWalk()
    {
        Harness h = new();
        h.Pack[Bliz.ItemNumber] = 1;

        Assert.True(h.Errand.Begin(9, Loop));
        h.FireTimers();

        Assert.Equal(new[] { "sp", "read scroll of blizzard" }, h.Sent);
        Assert.Empty(h.Walked);
        h.Learned(Bliz);
        Assert.Equal(new[] { "blizzard" }, h.Result!.Value.Learned);
    }

    [Fact]
    public void NoStockListStillTriesTheBuys()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Errand.Begin(7, Loop);
        h.FireTimers();
        h.Arrive(ShopA);

        h.FireTimers();   // the list window closes

        Assert.Equal("buy scroll of smite", h.Sent[^1]);
    }

    [Fact]
    public void AShopThatCannotBeReachedIsSkippedForTheNext()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Shops[ShopB] = new() { Flash };
        h.Errand.Begin(7, Loop);
        h.FireTimers();

        h.Errand.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", ShopA));

        Assert.Equal(new[] { ShopA, ShopB }, h.Walked);
    }

    [Fact]
    public void AWalkSomeoneElseStoppedEndsTheTripAsAborted()
    {
        Harness h = new();
        h.Shops[ShopA] = new() { Smite };
        h.Errand.Begin(7, Loop);
        h.FireTimers();

        h.Errand.OnWalkEvent(new WalkEvent(WalkEventKind.Stopped, "user", ShopA));

        Assert.True(h.Result!.Value.Aborted);
    }

    [Fact]
    public void ATruncatedListNameStillCountsAsInStock()
    {
        Harness h = new();
        ShopSpellPurchase disease = new(9, "cure major disease", 49, 109, "scroll of cure major disease", 250000);
        h.Shops[ShopA] = new() { disease };
        h.Errand.Begin(49, Loop);
        h.FireTimers();
        h.Arrive(ShopA);

        h.List(("scroll of cure major di", 1));

        Assert.Equal("buy scroll of cure major disease", h.Sent[^1]);
    }
}
