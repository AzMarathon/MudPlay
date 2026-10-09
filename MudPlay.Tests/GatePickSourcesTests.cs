using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The source tail each requirement on a route card carries says what that card's
// pick itself will do: a source for what the pick fetches (flagged Auto-obtain or
// not), nothing for a pick that only walks somewhere and stops, and a trade only
// for a door key.
public sealed class GatePickSourcesTests
{
    private const int Raft = 5, Key = 9, Counter = 11;

    private static readonly RoomKey Somewhere = new(1, 50);

    private sealed class Sources
    {
        public readonly HashSet<int> KeysWithOtherSource = new();
        public readonly HashSet<int> Flagged = new();
        public readonly Dictionary<int, GiveSource> Free = new();
        public readonly Dictionary<int, GiveSource> Trade = new();
        public readonly Dictionary<int, string> Shops = new();
        public readonly Dictionary<int, string> Droppers = new();
        public readonly Dictionary<int, string> TradeNotes = new();

        public GatePickSources Build(bool pickFetches, params RouteRequirement[] requirements) =>
            GatePickSources.Build(
                requirements, pickFetches,
                keyHasOtherSource: KeysWithOtherSource.Contains,
                flaggedAutoObtain: Flagged.Contains,
                giver: (id, offerTrades) =>
                    Free.TryGetValue(id, out GiveSource f) ? f
                    : offerTrades && Trade.TryGetValue(id, out GiveSource t) ? t
                    : null,
                buyPhrase: id => Shops.GetValueOrDefault(id),
                dropper: id => Droppers.GetValueOrDefault(id),
                tradeNote: id => TradeNotes.GetValueOrDefault(id));
    }

    private static RouteRequirement Carry(int id) => new(RouteRequirementKind.CarryItem, new[] { id });
    private static RouteRequirement DoorKey(int id) => new(RouteRequirementKind.DoorKey, new[] { id });

    // The pick force-obtains an unflagged gate item, so its card says where from,
    // whichever kind of source that is.
    [Fact]
    public void FetchingPick_UnflaggedGateItem_NamesItsSource_GiveThenShopThenDrop()
    {
        var s = new Sources();
        s.Shops[Raft] = "buy at General Store";
        s.Droppers[Raft] = "river troll";

        GatePickSources bought = s.Build(pickFetches: true, Carry(Raft));
        Assert.Null(bought.GiverName(Raft));
        Assert.Equal("buy at General Store", bought.BuyOrTradeNote(Raft));
        Assert.Null(bought.DropperName(Raft));

        s.Free[Raft] = new GiveSource(Somewhere, "ask ferryman raft", "ferryman");
        GatePickSources given = s.Build(pickFetches: true, Carry(Raft));
        Assert.Equal("ferryman", given.GiverName(Raft));
        Assert.Null(given.BuyOrTradeNote(Raft));

        s.Free.Clear();
        s.Shops.Clear();
        Assert.Equal("river troll", s.Build(pickFetches: true, Carry(Raft)).DropperName(Raft));
    }

    // A pick that walks to a hazard's edge, or to a shop it can't pay at, fetches
    // nothing, so it names no source, flagged or not.
    [Fact]
    public void StoppingPick_NamesNoSource_EvenForAFlaggedItem()
    {
        var s = new Sources();
        s.Flagged.Add(Raft);
        s.Free[Raft] = new GiveSource(Somewhere, "ask ferryman raft", "ferryman");
        s.Shops[Raft] = "buy at General Store";

        GatePickSources pick = s.Build(pickFetches: false, Carry(Raft));

        Assert.Null(pick.GiverName(Raft));
        Assert.Null(pick.BuyOrTradeNote(Raft));
        Assert.Null(pick.DropperName(Raft));
    }

    // A key nothing fetches is not on the pick's list, so it gets no source and
    // only the note of how it is come by.
    [Fact]
    public void FetchingPick_KeyWithNoSource_CarriesOnlyTheTradeNote()
    {
        var s = new Sources();
        s.TradeNotes[Key] = "a trader trades one for opal brooch";
        s.Shops[Key] = "buy at Locksmith";   // a helper that would answer is not asked

        GatePickSources pick = s.Build(pickFetches: true, DoorKey(Key));

        Assert.Null(pick.GiverName(Key));
        Assert.Equal("a trader trades one for opal brooch", pick.BuyOrTradeNote(Key));
        Assert.Empty(pick.Trades);
    }

    // A trade is offered for a door key and nothing else: another gate item is
    // never asked for by trade, though the same giver would make one.
    [Fact]
    public void Trade_IsForDoorKeysOnly()
    {
        var s = new Sources();
        var trader = new GiveSource(Somewhere, "ask trader deal", "trader, in trade for your brooch", TakesItemId: 33);
        s.Trade[Key] = trader;
        s.Trade[Raft] = trader;
        s.TradeNotes[Raft] = "trader trades one for your brooch";

        GatePickSources pick = s.Build(pickFetches: true, DoorKey(Key), Carry(Raft));

        Assert.Equal("trader, in trade for your brooch", pick.GiverName(Key));
        Assert.Equal(new[] { (Key, 33) }, pick.Trades);
        Assert.Null(pick.GiverName(Raft));
        Assert.Null(pick.BuyOrTradeNote(Raft));
    }

    // A free hand-over of a key is not a trade to agree to.
    [Fact]
    public void FreelyGivenKey_IsNotListedAsATrade()
    {
        var s = new Sources();
        s.KeysWithOtherSource.Add(Key);
        s.Free[Key] = new GiveSource(Somewhere, "ask hermit remind", "old hermit");

        GatePickSources pick = s.Build(pickFetches: true, DoorKey(Key));

        Assert.Equal("old hermit", pick.GiverName(Key));
        Assert.Empty(pick.Trades);
    }

    // A hazard counter the picker left unresolved keeps the Auto-obtain flag as its
    // rule, and an item already carried carries no tail at all.
    [Fact]
    public void HazardCounterFollowsTheFlag_AndACarriedItemIsSkipped()
    {
        var s = new Sources();
        s.Shops[Counter] = "buy at Outfitter";
        s.Shops[Raft] = "buy at General Store";
        var hazard = new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { Counter });
        var held = new RouteRequirement(RouteRequirementKind.CarryItem, new[] { Raft }) { Carried = true };

        Assert.Null(s.Build(pickFetches: true, hazard, held).BuyOrTradeNote(Counter));

        s.Flagged.Add(Counter);
        GatePickSources pick = s.Build(pickFetches: true, hazard, held);
        Assert.Equal("buy at Outfitter", pick.BuyOrTradeNote(Counter));
        Assert.Null(pick.BuyOrTradeNote(Raft));

        // A pick that stops short fetches no counter either.
        Assert.Null(s.Build(pickFetches: false, hazard).BuyOrTradeNote(Counter));
    }

    // The trade item is in the pack but the trader can't be reached on this walk:
    // the walk will fetch nothing for the key, so the card promises no source, not
    // even a lair the key also drops in, and keeps only the note of the trade.
    [Fact]
    public void KeyWhoseOnlyTraderIsOutOfReach_NamesNoSource()
    {
        var s = new Sources();
        s.Droppers[Key] = "dark cultist";
        s.TradeNotes[Key] = "a trader trades one for your brooch";

        GatePickSources pick = s.Build(pickFetches: true, DoorKey(Key));

        Assert.Null(pick.GiverName(Key));
        Assert.Null(pick.DropperName(Key));
        Assert.Equal("a trader trades one for your brooch", pick.BuyOrTradeNote(Key));
        Assert.Empty(pick.Trades);
    }

    // A key with a shop or a summon behind it is fetched whether or not a giver
    // resolves, and says where from.
    [Fact]
    public void KeyWithAnUntradedSource_NamesItsShop()
    {
        var s = new Sources();
        s.KeysWithOtherSource.Add(Key);
        s.Shops[Key] = "buy at Thieves' Guild";

        Assert.Equal("buy at Thieves' Guild", s.Build(pickFetches: true, DoorKey(Key)).BuyOrTradeNote(Key));
    }
}
