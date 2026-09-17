using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemUseCountTracker's stock charge-counting: an outbound `use <item>` for a
// limited-use item decrements remaining (= max − used); a RECHARGEABLE item restocks
// to max once the cleanup boundary passes; a FINITE item's count survives it; the
// counter is inert on Paradigm; and an infinite item reports no charges.
public sealed class ItemUseCountTrackerTests : IDisposable
{
    private const string ItemsJson = """
        [
          { "Number": 10, "Name": "gnarled wand",  "UseCount": 10, "Retain After Uses": 0 },
          { "Number": 20, "Name": "peasant cloak", "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 30, "Name": "nexus spear",   "UseCount": -1, "Retain After Uses": 0 }
        ]
        """;

    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly ProfileService _profile;
    private readonly List<string> _carried = new();
    private DateTimeOffset _clock = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);   // before the 21:00 cleanup
    private bool _stock = true;

    public ItemUseCountTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-usecount-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("alpha");
        _profile = new ProfileService();
        _profile.LoadBlank();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private ItemUseCountTracker NewTracker()
        => new(
            gameData: _cache,
            heldItems: () => _carried,
            itemNumberOf: name => name switch
            {
                "gnarled wand" => 10,
                "peasant cloak" => 20,
                "nexus spear" => 30,
                _ => 0,
            },
            onStock: () => _stock,
            cleanupConfig: () => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc),
            profile: _profile,
            now: () => _clock,
            log: null);

    private static void Use(ItemUseCountTracker t, string item)
        => t.ObserveOutbound(Encoding.Latin1.GetBytes($"use {item}\r"));

    [Fact]
    public void Stock_CountsUses_RemainingDecrements()
    {
        _carried.Add("gnarled wand");
        var t = NewTracker();
        Assert.Equal(10, t.RemainingFor(10));   // full before any use
        Use(t, "gnarled wand");
        Use(t, "gnarled wand");
        Use(t, "gnarled wand");
        Assert.Equal(7, t.RemainingFor(10));
    }

    [Fact]
    public void RechargeItem_RestocksToMax_AfterCleanup()
    {
        _carried.Add("peasant cloak");
        var t = NewTracker();
        Use(t, "peasant cloak");
        Use(t, "peasant cloak");
        Assert.Equal(3, t.RemainingFor(20));     // 5 − 2

        _clock = new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);   // past the 21:00 cleanup
        Assert.Equal(5, t.RemainingFor(20));     // restocked
    }

    [Fact]
    public void FiniteItem_CountPersists_AcrossCleanup()
    {
        _carried.Add("gnarled wand");
        var t = NewTracker();
        Use(t, "gnarled wand");
        Use(t, "gnarled wand");
        Use(t, "gnarled wand");
        Assert.Equal(7, t.RemainingFor(10));

        _clock = new DateTimeOffset(2026, 1, 2, 22, 0, 0, TimeSpan.Zero);   // a day later, past cleanup
        Assert.Equal(7, t.RemainingFor(10));     // no recharge — stays spent
    }

    [Fact]
    public void OnParadigm_DoesNotCount()
    {
        _carried.Add("gnarled wand");
        _stock = false;                          // Paradigm reads charges from the look reply
        var t = NewTracker();
        Use(t, "gnarled wand");
        Use(t, "gnarled wand");
        Assert.Equal(10, t.RemainingFor(10));    // uncounted
    }

    [Fact]
    public void InfiniteItem_ReportsNoCharges()
    {
        _carried.Add("nexus spear");
        var t = NewTracker();
        Use(t, "nexus spear");
        Assert.Null(t.RemainingFor(30));
    }

    [Fact]
    public void UseWithTarget_StillResolvesTheItem()
    {
        _carried.Add("gnarled wand");
        var t = NewTracker();
        t.ObserveOutbound(Encoding.Latin1.GetBytes("use gnarled wand orc\r"));   // item + target
        Assert.Equal(9, t.RemainingFor(10));
    }
}
