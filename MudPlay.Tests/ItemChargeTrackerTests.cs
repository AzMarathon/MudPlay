using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemChargeTracker's Paradigm charge model: a `look <item>` reply's "Uses
// remaining: N" is recorded against the carried item by number and PERSISTED on the
// profile; RemainingFor is recharge-adjusted (rechargeable restocks to max past
// cleanup, finite persists); an unknown charged item is auto-looked; and a `use`
// reconciles by re-looking (so a blocked use never mis-decrements).
public sealed class ItemChargeTrackerTests : IDisposable
{
    private const string ItemsJson = """
        [
          { "Number": 10, "Name": "gnarled wand",        "UseCount": 10, "Retain After Uses": 0 },
          { "Number": 20, "Name": "peasant cloak",       "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 30, "Name": "nexus spear",         "UseCount": -1, "Retain After Uses": 0 },
          { "Number": 40, "Name": "token of Silvermere", "UseCount": 0,  "Retain After Uses": 1 }
        ]
        """;

    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly ProfileService _profile;
    private readonly List<string> _carried = new();
    private readonly List<string> _sent = new();
    private readonly List<Action> _scheduled = new();
    private DateTimeOffset _clock = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);   // before the 21:00 cleanup
    private bool _paradigm = true;
    private readonly ItemChargeTracker _tracker;

    public ItemChargeTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemcharge-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("alpha");
        _profile = new ProfileService();
        _profile.LoadBlank();

        _tracker = new ItemChargeTracker(
            gameData: _cache,
            profile: _profile,
            carried: () => _carried,
            itemNumberOf: Number,
            onParadigm: () => _paradigm,
            cleanupConfig: () => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc),
            // Mirror production: SendGameCommand re-enters the outbound tap, so a look
            // sent by the tracker arms its own capture.
            sendLook: cmd => { _sent.Add(cmd); _tracker!.ObserveOutbound(Bytes(cmd)); },
            schedule: (_, a) => _scheduled.Add(a),
            now: () => _clock,
            log: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static int Number(string name) => name switch
    {
        "gnarled wand" => 10,
        "peasant cloak" => 20,
        "nexus spear" => 30,
        "token of Silvermere" => 40,
        _ => 0,
    };

    private static byte[] Bytes(string cmd) => Encoding.Latin1.GetBytes(cmd + "\r");
    private void Look(string cmd) => _tracker.ObserveOutbound(Bytes(cmd));
    private void Use(string cmd) => _tracker.ObserveOutbound(Bytes(cmd));
    private void Line(string text) => _tracker.HandleLine(text);
    // Fire the timers due "now" — one tick. Actions those fire in turn (e.g. a re-look's
    // reply window) land in the queue for a later FireTimers, mirroring real timer
    // ordering so a same-tick window-clear can't wipe a capture the reply just armed.
    private void FireTimers()
    {
        List<Action> due = new(_scheduled);
        _scheduled.Clear();
        foreach (Action a in due) a();
    }

    [Fact]
    public void Look_ThenUsesRemaining_RecordsAndPersists()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");                     // partial resolves to the wand
        Line("A crooked stick.");
        Line("Uses remaining: 7");
        Assert.Equal(7, _tracker.RemainingFor(10));
        Assert.Equal(7, _profile.Current!.ItemCharges![10].Remaining);   // persisted by number
    }

    [Fact]
    public void RechargeItem_RestocksToMax_AfterCleanup()
    {
        _carried.Add("peasant cloak");
        Look("look peasant");
        Line("Uses remaining: 2");
        Assert.Equal(2, _tracker.RemainingFor(20));

        _clock = new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);   // past the 21:00 cleanup
        Assert.Equal(5, _tracker.RemainingFor(20));                        // assumed restocked to max
    }

    [Fact]
    public void FiniteItem_CountPersists_AcrossCleanup()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 4");
        _clock = new DateTimeOffset(2026, 1, 2, 22, 0, 0, TimeSpan.Zero);   // a day later, past cleanup
        Assert.Equal(4, _tracker.RemainingFor(10));                        // no recharge — stays spent
    }

    [Fact]
    public void UnknownChargedItem_IsAutoLooked()
    {
        _carried.Add("gnarled wand");
        _tracker.EnsureChargesKnown();
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 9");                // the auto-look armed capture; its reply lands
        Assert.Equal(9, _tracker.RemainingFor(10));

        _sent.Clear();
        FireTimers();                             // advance the queue
        _tracker.EnsureChargesKnown();            // now known — no second look
        Assert.DoesNotContain("look gnarled wand", _sent);
    }

    [Fact]
    public void InfiniteItem_IsNotAutoLooked()
    {
        _carried.Add("nexus spear");
        _tracker.EnsureChargesKnown();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Use_ReLooks_AndReconcilesToTheTrueCount()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 5");

        _sent.Clear();
        Use("use gnarled wand");                  // schedules the reconciling re-look
        FireTimers();                             // fire the debounce → look sent (arms capture)
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 4");                // game says one fewer
        Assert.Equal(4, _tracker.RemainingFor(10));
    }

    [Fact]
    public void BlockedUse_DoesNotMisDecrement()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 5");

        Use("use gnarled wand");                  // the use is blocked in-game
        FireTimers();
        Line("Uses remaining: 5");                // re-look shows the count unchanged
        Assert.Equal(5, _tracker.RemainingFor(10));
    }

    [Fact]
    public void TokenLook_RecordsEvenWhenGameDataUseCountIsZero()
    {
        _carried.Add("token of Silvermere");
        Look("look token of Silvermere");         // the shape TokenTracker sends on login
        Line("Uses remaining: 3");
        Assert.Equal(3, _tracker.RemainingFor(40));
    }

    [Fact]
    public void OffParadigm_IsInert()
    {
        _paradigm = false;
        _carried.Add("gnarled wand");
        _tracker.EnsureChargesKnown();
        Look("look gnarled");
        Line("Uses remaining: 5");
        Assert.Empty(_sent);
        Assert.Null(_tracker.RemainingFor(10));
    }

    [Fact]
    public void ResetSession_KeepsPersistedCounts()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 6");
        _tracker.ResetSession();
        Assert.Equal(6, _tracker.RemainingFor(10));   // store lives on the profile
    }
}
