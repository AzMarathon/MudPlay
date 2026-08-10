using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ParadigmMovementCostModel evaluates the ParaMUD movement-speed formula
// against a live inventory snapshot (carry weight + worn quickness) and adds a
// fixed per-hop latency. These tests pin the formula math, the 1-second cap
// clamp, the zero/negative-hop guard, the latency floor, and the live-snapshot
// tracking that lets the estimate follow the player picking up loot / swapping
// gear without a model re-construct.
public sealed class ParadigmMovementCostModelTests : IDisposable
{
    private readonly string _root;

    public ParadigmMovementCostModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-paramove-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Seeds an isolated Items table and activates it, so worn-item quickness
    // resolves against real game-data rows (Abil 67 = Quickness).
    private GameDataCache CacheWithItems(params Dictionary<string, object>[] items)
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(items));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static Dictionary<string, object> QuicknessItem(string name, int quickness) =>
        new() { ["Name"] = name, ["Abil-0"] = 67, ["AbilVal-0"] = quickness };

    private static InventorySnapshot Snapshot(int encPercent, params EquippedItem[] worn) =>
        new(
            CurrencyHoldings.Empty,
            new EncumbranceReading(0, 0, encPercent, EncumbranceLevel.Unknown),
            worn,
            Array.Empty<string>(),
            DateTimeOffset.UtcNow);

    // Millisecond precision — the 0.35 s latency addend makes exact tick
    // equality fragile (TimeSpan truncates sub-tick), so compare seconds to 3dp.
    private const int MsPrecision = 3;

    [Fact]
    public void EmptyGear_UnencumberedIsBaseSpeedPlusLatency()
    {
        // speed = 1100 ms base, above the 1000 cap so no clamp, + 0.35 s latency.
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(0), cache);
        Assert.Equal(1.45, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }

    [Fact]
    public void SpareQuickness_ClampsToOneSecondCap()
    {
        // enc0 + 20 quickness → 1100 - 200 = 900 ms, below the 1000 cap → clamp
        // to 1.0 s, + 0.35 latency = 1.35 s.
        GameDataCache cache = CacheWithItems(QuicknessItem("boots of speed", 20));
        ParadigmMovementCostModel model = new(
            () => Snapshot(0, new EquippedItem("boots of speed", "Feet")), cache);
        Assert.Equal(1.35, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }

    [Fact]
    public void Encumbrance_RaisesPerHopEstimate()
    {
        // enc 50% → 1100 + 0.5² × 2000 = 1600 ms, + 0.35 latency = 1.95 s.
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(50), cache);
        Assert.Equal(1.95, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ZeroOrNegativeHops_ReturnsZero(int hops)
    {
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(0), cache);
        Assert.Equal(TimeSpan.Zero, model.EstimateTravel(hops));
    }

    [Fact]
    public void CustomLatency_AddsOnTopOfFormula()
    {
        // 1.1 s formula timer + 1.0 s custom latency = 2.1 s.
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(0), cache, latencySeconds: 1.0);
        Assert.Equal(2.1, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }

    [Fact]
    public void NegativeLatency_FlooredToZero()
    {
        // A negative latency would shave real travel time — the model floors at 0.
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(0), cache, latencySeconds: -5.0);
        Assert.Equal(1.1, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }

    [Fact]
    public void MultipleHops_ScaleLinearly()
    {
        // 3 hops × 1.45 s = 4.35 s.
        GameDataCache cache = CacheWithItems();
        ParadigmMovementCostModel model = new(() => Snapshot(0), cache);
        Assert.Equal(4.35, model.EstimateTravel(3).TotalSeconds, MsPrecision);
    }

    [Fact]
    public void LiveSnapshot_TracksEncumbranceChange()
    {
        // The model reads the provider on every call — a heavier pack between
        // calls raises the estimate without re-constructing the model.
        GameDataCache cache = CacheWithItems();
        int encPercent = 0;
        ParadigmMovementCostModel model = new(() => Snapshot(encPercent), cache);
        Assert.Equal(1.45, model.EstimateTravel(1).TotalSeconds, MsPrecision);

        encPercent = 50;
        Assert.Equal(1.95, model.EstimateTravel(1).TotalSeconds, MsPrecision);
    }
}
