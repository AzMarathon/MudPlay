using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// AutoSellManager: arriving in a shop room whose shop trades a carried AutoSell item
// sells it down to its keep floor — no `list` — one `sell` per copy on Stock,
// advancing off the live "You sold ..." / "You cannot sell ... here." result.
public sealed class AutoSellManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoSellManager Sell { get; }
        public List<byte[]> Sent { get; } = new();
        public List<string> Carried { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Paradigm { get; set; }
        public int TradingShop { get; set; } = 7;

        // name -> (Number, Sell, KeepCount)
        private readonly Dictionary<string, (int Number, bool Sell, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Sell = new AutoSellManager(Router,
                carriedItems: () => Carried,
                resolve: Resolve,
                shopTradesItem: (shop, item) => shop == TradingShop,
                isEnabled: () => Enabled,
                log: Log,
                isParadigm: () => Paradigm);
            Sell.SetWireSender(b => Sent.Add(b));
        }

        public void Map(string name, int number, bool sell, int keep = 0)
            => _map[name] = (number, sell, keep);

        private AutoSellManager.ResolvedSell? Resolve(string entry)
            => _map.TryGetValue(entry.Trim(), out (int Number, bool Sell, int Keep) v)
                ? new AutoSellManager.ResolvedSell(v.Number, entry.Trim(), v.Sell, v.Keep)
                : null;

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void EnterShop(int shop = 7) => Sell.OnRoomEntered(shop);

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Sell.Dispose();
    }

    [Fact]
    public void FlaggedItem_SellsEveryCopy_OnePerResult()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.EnterShop();
        Assert.Equal(new[] { "sell dagger" }, h.SentText);   // one at a time

        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");

        Assert.Equal(3, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("sell dagger", s));
    }

    [Fact]
    public void Paradigm_SellsWholeQuantityInOneCountedCommand()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.EnterShop();
        Assert.Equal(new[] { "sell 3 dagger" }, h.SentText);   // one batched command

        // The counted confirmation drains all three; the pump finishes without a
        // second send.
        h.Feed("You sold 3 dagger for 15 gold crowns.");

        Assert.Single(h.SentText);
    }

    [Fact]
    public void KeepFloor_LeavesMinimum()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true, keep: 1);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.EnterShop();
        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");
        // Would-be third result never comes because only two were queued.

        Assert.Equal(2, h.SentText.Count);   // 3 carried − 1 keep
    }

    [Fact]
    public void UnflaggedItem_NoSell()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: false);
        h.Carried.Add("dagger");

        h.EnterShop();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ShopRefuses_AbandonsThatItem()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.EnterShop();
        Assert.Single(h.Sent);                       // first sell attempt

        h.Feed("You cannot sell dagger here.");       // this shop won't buy it

        Assert.Single(h.Sent);                       // no further attempts
    }

    [Fact]
    public void DisabledMaster_NoSell()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, sell: true);
        h.Carried.Add("dagger");

        h.EnterShop();

        Assert.Empty(h.Sent);
    }

    // Walking into a shop that doesn't have the item in its listing sells nothing.
    [Fact]
    public void ShopThatDoesntTradeIt_NoSell()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.Add("dagger");

        h.EnterShop(shop: 9);
        h.Sell.OnRoomEntered(0);

        Assert.Empty(h.Sent);
    }

    // The walk waits on the Selling gate until the last result lands.
    [Fact]
    public void HoldsMovementUntilSold()
    {
        using Harness h = new();
        Game.Map.MovementCoordinator coord = new(h.Log);
        h.Sell.SetMovementGate(coord);
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.EnterShop();
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SellingGate));
        h.Feed("You sold dagger for 5 gold crowns.");
        Assert.True(coord.IsGateAsserted(Game.Map.MovementCoordinator.SellingGate));
        h.Feed("You sold dagger for 5 gold crowns.");

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SellingGate));
        Assert.False(h.Sell.IsSelling);
    }

    // A result that never comes back lets the walk go.
    [Fact]
    public void NoResult_TimesOutAndReleases()
    {
        using Harness h = new();
        Game.Map.MovementCoordinator coord = new(h.Log);
        List<Action> later = new();
        h.Sell.SetMovementGate(coord);
        h.Sell.SetScheduler((_, a) => later.Add(a));
        h.Map("dagger", 1, sell: true);
        h.Carried.Add("dagger");

        h.EnterShop();
        later[^1]();

        Assert.False(coord.IsGateAsserted(Game.Map.MovementCoordinator.SellingGate));
    }
}
