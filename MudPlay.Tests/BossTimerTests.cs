using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// Boss respawn-timer math (per realm, expiry, exact-spawn), the persisted
// BossTimerStore (mark / reset / kill-detection / active list), and the @timer
// remote report (format / substring filter / "expired").
public sealed class BossTimerTests : IDisposable
{
    private readonly string _set = "boss-timer-test-" + Path.GetRandomFileName();
    private readonly string _seedPath =
        Path.Combine(Path.GetTempPath(), "bts-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { string d = AppPaths.GameDataSetDir(_set); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); } catch { }
    }

    // ----- BossTimerMath (pure) ---------------------------------------------

    [Theory]
    [InlineData(0.80, "-20%")]
    [InlineData(0.90, "-10%")]
    [InlineData(0.95, "-5%")]
    [InlineData(1.00, "full")]
    public void WindowLabel_Paradigm_UsesDiscount(double f, string expected)
        => Assert.Equal(expected, BossTimerMath.WindowLabel(RealmType.ParaMud, f));

    [Theory]
    [InlineData(0.875, "87.5%")]
    [InlineData(1.00, "full")]
    public void WindowLabel_Stock_UsesElapsedFraction(double f, string expected)
        => Assert.Equal(expected, BossTimerMath.WindowLabel(RealmType.Stock, f));

    [Fact]
    public void Describe_Paradigm_WalksThresholdsInOrder()
    {
        // full = 24h. Early points at 19.2 / 21.6 / 22.8h, then 24h.
        Assert.Equal("-20%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(0)).NextLabel);
        Assert.Equal("-10%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(20)).NextLabel);
        Assert.Equal("-5%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(22)).NextLabel);
        Assert.Equal("full", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(23)).NextLabel);
    }

    [Fact]
    public void Describe_Stock_HasSingleEarlyPoint()
    {
        Assert.Equal("87.5%", BossTimerMath.Describe(RealmType.Stock, 24, TimeSpan.FromHours(0)).NextLabel);
        Assert.Equal("full", BossTimerMath.Describe(RealmType.Stock, 24, TimeSpan.FromHours(22)).NextLabel);
    }

    [Fact]
    public void Describe_PastFullTimer_IsExpired()
    {
        Assert.True(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(24)).Expired);
        Assert.True(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(30)).Expired);
        Assert.False(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(23.9)).Expired);
    }

    [Fact]
    public void Describe_FullRemaining_CountsDownToGuaranteedSpawn()
    {
        BossWindowState s = BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(6));
        Assert.Equal(18, Math.Round(s.FullRemaining.TotalHours));
    }

    // ----- BossTimerStore ----------------------------------------------------

    private void SeedGameData(RealmType realm, params (string Name, int Number, int Regen, int GameLimit)[] monsters)
    {
        string dir = AppPaths.GameDataSetDir(_set);
        Directory.CreateDirectory(dir);
        var rows = monsters.Select(m => new
        {
            m.Name,
            m.Number,
            RegenTime = m.Regen,
            GameLimit = m.GameLimit,
        });
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), JsonSerializer.Serialize(rows));
        File.WriteAllText(Path.Combine(dir, "Info.json"),
            JsonSerializer.Serialize(new[] { new { Legit = realm == RealmType.ParaMud ? 2 : 0 } }));
    }

    private void SeedBosses(params BossDef[] defs) => JsonStore.Save(_seedPath, defs.ToList());

    private static BossDef Boss(string name, int? number = 1, BossRespawnType type = BossRespawnType.Timed,
        bool stock = true, bool para = true, params string[] rooms) => new()
    {
        Name = name, MonsterNumber = number, Rooms = rooms.ToList(),
        InStock = stock, InParadigm = para, RespawnType = type,
    };

    private (BossStore bosses, BossTimerStore timers, GameDataCache cache) NewStores()
    {
        GameDataCache cache = new();
        cache.SwitchSet(_set);
        BossStore bosses = new(seedPath: _seedPath);
        bosses.OnActiveSetChanged(_set);
        BossTimerStore timers = new(bosses, cache);
        timers.OnActiveSetChanged(_set);
        return (bosses, timers, cache);
    }

    [Fact]
    public void MarkKilled_StartsActiveTimer_AndPersistsAcrossReload()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ogre king");
        Assert.NotNull(timers.StatusFor(Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud));

        // A fresh store for the same set reloads the persisted kill time.
        BossStore bosses2 = new(seedPath: _seedPath); bosses2.OnActiveSetChanged(_set);
        GameDataCache cache2 = new(); cache2.SwitchSet(_set);
        BossTimerStore reloaded = new(bosses2, cache2);
        reloaded.OnActiveSetChanged(_set);
        Assert.NotNull(reloaded.KilledAt("ogre king"));
    }

    [Fact]
    public void BossRow_LastKilled_ReflectsTimerStore()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        BossRowViewModel row = new(
            Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud, 24, timers,
            onEdit: () => { }, onMarkRequested: _ => { });

        // No kill recorded yet → blank + sentinel sort key (sorts last).
        Assert.Equal(string.Empty, row.LastKilledDisplay);
        Assert.Equal(long.MinValue, row.LastKilledSortKey);

        // A recorded kill (button or auto-detect, both land on MarkKilled) surfaces.
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-30);
        timers.MarkKilled("ogre king", at);
        row.RefreshStatus();

        Assert.NotEqual(string.Empty, row.LastKilledDisplay);
        Assert.Equal(at.ToUnixTimeSeconds(), row.LastKilledSortKey);
    }

    [Fact]
    public void Reset_ClearsTimer()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ogre king");
        timers.Reset("ogre king");
        Assert.Null(timers.KilledAt("ogre king"));
        Assert.Null(timers.StatusFor(Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud));
    }

    [Fact]
    public void StatusFor_Null_ForCleanupBoss()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("lord feyr");
        Assert.Null(timers.StatusFor(
            Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"), RealmType.ParaMud));
    }

    [Fact]
    public void StatusFor_Null_WhenGameDataHasNoTimer()
    {
        SeedGameData(RealmType.ParaMud, ("some mob", 1, 0, 0));   // not a boss (no regen, no limit)
        SeedBosses(Boss("ghost king", number: 999, rooms: "1/1"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ghost king");
        Assert.Null(timers.StatusFor(Boss("ghost king", number: 999, rooms: "1/1"), RealmType.ParaMud));
    }

    private static MonsterDeathEvent Death(bool fallback, params (int? Number, string Name)[] candidates) => new(
        candidates.Select(c => new MonsterDeathIdentity(c.Number, c.Name)).ToList(),
        ExperienceGained: 100, At: DateTimeOffset.UtcNow, IsFallback: fallback);

    [Fact]
    public void OnMonsterDied_EngagedNameInBossRoom_StartsTimer()
    {
        // The canonical signal: engaged the boss by name, then a (fallback, no
        // candidate) death fires in its room.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(3, 300), engagedName: "ogre king");
        Assert.NotNull(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_EngagedNameWithArticle_Matches()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(3, 300), engagedName: "The Ogre King");
        Assert.NotNull(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_SpecificCandidateName_StartsTimer()
    {
        // Secondary path: a specific death line names the boss even without a live
        // engaged-target name.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(false, (null, "ogre king")), new RoomKey(3, 300), engagedName: null);
        Assert.NotNull(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_NoEngagedNameNoCandidate_Ignored()
    {
        // A fallback death with nothing to attribute it to — left to manual override.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(3, 300), engagedName: null);
        Assert.Null(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_EngagedNameNotABoss_Ignored()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(3, 300), engagedName: "giant rat");
        Assert.Null(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_WrongRoom_Ignored()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(9, 999), engagedName: "ogre king");
        Assert.Null(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void ActiveTimers_FiltersByRealm_AndOrdersBySoonestFull()
    {
        SeedGameData(RealmType.ParaMud,
            ("short boss", 1, 2, 1),
            ("long boss", 2, 48, 1),
            ("para only", 3, 10, 1));
        SeedBosses(
            Boss("short boss", number: 1, rooms: "1/1"),
            Boss("long boss", number: 2, rooms: "1/2"),
            Boss("para only", number: 3, stock: false, para: true, rooms: "1/3"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("short boss");
        timers.MarkKilled("long boss");
        timers.MarkKilled("para only");

        var para = timers.ActiveTimers(RealmType.ParaMud);
        Assert.Equal(new[] { "short boss", "para only", "long boss" },
            para.Select(a => a.Def.Name).ToArray());   // ordered by soonest full (2h < 10h < 48h)

        // Stock hides the paradigm-only boss.
        var stock = timers.ActiveTimers(RealmType.Stock);
        Assert.DoesNotContain(stock, a => a.Def.Name == "para only");
    }

    // ----- @timer handler ----------------------------------------------------

    private static readonly DateTime Now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, BossTimerStore timers) SetupHandler(
        RealmType realm, params (string Name, int Number, int Regen)[] bosses)
    {
        SeedGameData(realm, bosses.Select(b => (b.Name, b.Number, b.Regen, 1)).ToArray());
        SeedBosses(bosses.Select(b => Boss(b.Name, number: b.Number, rooms: "1/1")).ToArray());
        var (store, timers, cache) = NewStores();

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        players.RecordObservation("Bob", null, null, null, null, null, null, Now);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryBossTimers));
        _ = new BossTimerQueryHandler(engine, store, timers, cache);
        return (engine, timers);
    }

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, "Bob", msg, $"Bob telepaths: {msg}");

    private static string Reply(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).Select(StripWire).Single();

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).Select(StripWire).ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    [Fact]
    public void Timer_NoneActive_ReportsEmptySet()
    {
        var (engine, _) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        engine.DispatchForTests(Telepath("@timer"));
        Assert.Equal("no boss timers active", Reply(engine));
    }

    [Fact]
    public void Timer_NoArg_ListsActiveBossWithFullAndNext()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.DispatchForTests(Telepath("@timer"));

        string reply = Reply(engine);
        Assert.Contains("ogre king", reply);
        Assert.Contains("full", reply);
        Assert.Contains("-20%", reply);   // freshly killed → earliest paradigm window
    }

    [Fact]
    public void Timer_SubstringFilter_MatchesByName()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("crimson mist", 10, 12), ("ogre king", 50, 24));
        timers.MarkKilled("crimson mist");
        timers.MarkKilled("ogre king");

        engine.DispatchForTests(Telepath("@timer crimson"));
        string reply = Reply(engine);
        Assert.Contains("crimson mist", reply);
        Assert.DoesNotContain("ogre king", reply);
    }

    [Fact]
    public void Timer_QueryWeDoNotHold_ReportsExpired()
    {
        var (engine, _) = SetupHandler(RealmType.ParaMud, ("crimson mist", 10, 12));
        engine.DispatchForTests(Telepath("@timer crimson"));
        Assert.Equal("expired", Reply(engine));
    }

    [Fact]
    public void Timer_MultiMatch_RepliesOnePerLine()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud,
            ("great green dragon", 10, 12), ("huge black dragon", 20, 24), ("ogre king", 50, 24));
        timers.MarkKilled("great green dragon");
        timers.MarkKilled("huge black dragon");
        timers.MarkKilled("ogre king");

        engine.DispatchForTests(Telepath("@timer dragon"));

        List<string> replies = Replies(engine);
        Assert.Equal(2, replies.Count);   // two dragons, two lines — ogre king excluded
        Assert.Contains(replies, r => r.Contains("great green dragon"));
        Assert.Contains(replies, r => r.Contains("huge black dragon"));
        Assert.DoesNotContain(replies, r => r.Contains("ogre king"));
    }

    [Fact]
    public void Timer_MoreThanFiveMatches_CapsWithKeywordOverflow()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud,
            ("red dragon", 1, 12), ("blue dragon", 2, 12), ("green dragon", 3, 12),
            ("black dragon", 4, 12), ("white dragon", 5, 12), ("gold dragon", 6, 12));
        foreach (string n in new[] { "red dragon", "blue dragon", "green dragon", "black dragon", "white dragon", "gold dragon" })
            timers.MarkKilled(n);

        engine.DispatchForTests(Telepath("@timer dragon"));

        List<string> replies = Replies(engine);
        Assert.Equal(6, replies.Count);   // 5 timer lines + 1 overflow line
        Assert.Contains("1 more timers matching 'dragon'", replies.Last());
    }

    [Fact]
    public void FormatHours_UsesHoursMinutesStyle()
    {
        Assert.Equal("2h14m", BossTimerMath.FormatHours(2 + 14 / 60.0));
        Assert.Equal("45m", BossTimerMath.FormatHours(0.75));
        Assert.Equal("0m", BossTimerMath.FormatHours(0));
    }

    // ----- mark-time + display-order helpers ---------------------------------

    [Fact]
    public void MarkKilled_WithExplicitTime_StoresThatInstant()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        timers.MarkKilled("ogre king", when);
        Assert.Equal(when, timers.KilledAt("ogre king"));
    }

    [Fact]
    public void EarlyFractionsAndLabels_AreRealmSpecific()
    {
        Assert.Equal(new[] { 0.95, 0.90, 0.80 }, BossTimerMath.EarlyFractionsInDisplayOrder(RealmType.ParaMud));
        Assert.Equal(new[] { 0.875 }, BossTimerMath.EarlyFractionsInDisplayOrder(RealmType.Stock));
        Assert.Equal(new[] { "-5%", "-10%", "-20%" }, BossTimerMath.EarlyColumnLabels(RealmType.ParaMud));
        Assert.Equal(new[] { "87.5%" }, BossTimerMath.EarlyColumnLabels(RealmType.Stock));
    }

    // ----- row VM live columns (blank-when-expired) --------------------------

    private BossRowViewModel Row(BossTimerStore timers, RealmType realm, int hours)
    {
        var def = Boss("ogre king", number: 50, rooms: "3/300");
        return new BossRowViewModel(def, realm, hours, timers, () => { }, _ => { });
    }

    [Fact]
    public void Row_EarlyWindow_BlanksOncePassed_WhileFullStillCounts()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        // Killed 20h ago on a 24h timer: 20% window (19.2h) has passed; 10% (21.6h)
        // and 5% (22.8h) are still counting, as is 100% (24h).
        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(20));
        row.RefreshStatus();

        Assert.True(row.IsActive);
        Assert.NotEqual(string.Empty, row.StatusDisplay);
        Assert.NotEqual(string.Empty, row.Early1Display);   // 5% — pending
        Assert.NotEqual(string.Empty, row.Early2Display);   // 10% — pending
        Assert.Equal(string.Empty, row.Early3Display);      // 20% — passed
    }

    [Fact]
    public void Row_NearFull_AllEarlyWindowsBlank_FullStillCounts()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        // 23h elapsed: every early window (19.2 / 21.6 / 22.8h) has passed; only the
        // 100% guaranteed spawn is still counting.
        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(23));
        row.RefreshStatus();

        Assert.True(row.IsActive);
        Assert.NotEqual(string.Empty, row.StatusDisplay);
        Assert.Equal(string.Empty, row.Early1Display);
        Assert.Equal(string.Empty, row.Early2Display);
        Assert.Equal(string.Empty, row.Early3Display);
    }

    [Fact]
    public void Row_PastFullTimer_IsInactive_AllBlank()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));
        row.RefreshStatus();

        Assert.False(row.IsActive);
        Assert.Equal(string.Empty, row.StatusDisplay);
        Assert.Equal(long.MaxValue, row.FullSortKey);
    }

    // ----- Mark + Manage dialogs (VM logic) ----------------------------------

    [Fact]
    public void MarkDialog_Ok_ReturnsCombinedDateAndTime()
    {
        var vm = new MarkTimerDialogViewModel("ogre king", new DateTimeOffset(2026, 8, 1, 10, 30, 0, TimeSpan.Zero));
        DateTimeOffset? got = null;
        bool fired = false;
        vm.CloseRequested += r => { got = r; fired = true; };

        vm.OkCommand.Execute(null);

        Assert.True(fired);
        Assert.NotNull(got);
        Assert.Equal(2026, got!.Value.Year);
        Assert.Equal(10, got.Value.Hour);
        Assert.Equal(30, got.Value.Minute);
    }

    [Fact]
    public void MarkDialog_Cancel_ReturnsNull()
    {
        var vm = new MarkTimerDialogViewModel("ogre king", DateTimeOffset.Now);
        bool fired = false;
        DateTimeOffset? got = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        vm.CloseRequested += r => { got = r; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(got);
    }

    [Fact]
    public void ManageDialog_Add_Save_PersistsNewBoss()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        Assert.Single(vm.Rows);   // ogre king loaded for the realm
        vm.AddRowCommand.Execute(null);
        ManageBossRowViewModel added = vm.Rows.Cast<ManageBossRowViewModel>().Last();
        added.Name = "new boss";
        added.Rooms = "1/100";
        added.InParadigm = true;

        bool? closed = null;
        vm.CloseRequested += r => closed = r;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.Contains(bosses.Resolve(), b => b.Name == "new boss");
    }

    [Fact]
    public void ManageDialog_Remove_Save_DropsBoss()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("chimera", 60, 12, 1));
        SeedBosses(
            Boss("ogre king", number: 50, rooms: "3/300"),
            Boss("chimera", number: 60, rooms: "3/583"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        ManageBossRowViewModel drop = vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "chimera");
        vm.RemoveRowCommand.Execute(drop);
        vm.SaveCommand.Execute(null);

        Assert.DoesNotContain(bosses.Resolve(), b => b.Name == "chimera");
        Assert.Contains(bosses.Resolve(), b => b.Name == "ogre king");
    }

    [Fact]
    public void ManageDialog_Cancel_ReturnsFalse_NoWrite()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        vm.AddRowCommand.Execute(null);
        vm.Rows.Cast<ManageBossRowViewModel>().Last().Name = "should not persist";
        bool? closed = null;
        vm.CloseRequested += r => closed = r;
        vm.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.DoesNotContain(bosses.Resolve(), b => b.Name == "should not persist");
    }

    [Fact]
    public void ManageDialog_RespawnOverride_FillsInWhenGameDataHasNoTimer()
    {
        // "ghost king" has no game-data monster, so the tab shows "?" and StatusFor
        // can't resolve a timer — until a manual respawn override is entered.
        SeedGameData(RealmType.ParaMud, ("some mob", 1, 0, 0));
        SeedBosses(Boss("ghost king", number: 999, rooms: "1/1"));
        var (bosses, timers, cache) = NewStores();
        timers.MarkKilled("ghost king");

        BossDef before = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ghost king");
        Assert.Null(timers.StatusFor(before, RealmType.ParaMud));   // no timer resolvable

        var vm = new ManageBossesDialogViewModel(bosses, cache);
        vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "ghost king").RespawnHoursText = "10";
        vm.SaveCommand.Execute(null);

        BossDef after = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ghost king");
        Assert.Equal(10, after.RespawnHoursOverride);
        Assert.NotNull(timers.StatusFor(after, RealmType.ParaMud));   // override drives the timer
    }

    [Fact]
    public void ManageDialog_ShowInTable_Unchecked_Persists_ButBossStillResolves()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "ogre king").ShowInTable = false;
        vm.SaveCommand.Execute(null);

        // Hidden from the tab, but still present in the store (tracked for @timer etc).
        BossDef def = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ogre king");
        Assert.False(def.ShowInTable);
    }

    // ----- cleanup bosses (DEAD / ALIVE) -------------------------------------

    [Fact]
    public void NextCleanup_IsFirstCleanupAfterKill()
    {
        var tz = TimeZoneInfo.Utc;
        var cleanup = TimeSpan.FromHours(21);

        // Killed at 20:00 → today's 21:00.
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 21, 0, 0, TimeSpan.Zero),
            BossTimerMath.NextCleanup(new DateTimeOffset(2026, 8, 5, 20, 0, 0, TimeSpan.Zero), cleanup, tz));
        // Killed at 22:00 (after cleanup) → tomorrow's 21:00.
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero),
            BossTimerMath.NextCleanup(new DateTimeOffset(2026, 8, 5, 22, 0, 0, TimeSpan.Zero), cleanup, tz));
    }

    private (BossStore bosses, BossTimerStore timers) CleanupStores()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (bosses, timers, _) = NewStores();
        timers.SetCleanupConfig(() => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc));
        return (bosses, timers);
    }

    [Fact]
    public void Cleanup_MarkedNow_IsDead_MarkedLongAgo_IsAlive()
    {
        var (_, timers) = CleanupStores();

        // Just marked: the next cleanup is always in the future → DEAD.
        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow);
        Assert.True(timers.IsCleanupDead("lord feyr"));

        // Marked over a day ago: the cleanup that clears it has passed → ALIVE.
        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));
        Assert.False(timers.IsCleanupDead("lord feyr"));
    }

    [Fact]
    public void Cleanup_NoConfig_StaysDeadUntilCleared()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (_, timers, _) = NewStores();   // no cleanup config set

        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow - TimeSpan.FromDays(3));
        Assert.True(timers.IsCleanupDead("lord feyr"));   // can't compute a flip → stays dead
    }

    [Fact]
    public void Row_Cleanup_ShowsAliveThenDead()
    {
        var (_, timers) = CleanupStores();
        var def = Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718");
        var row = new BossRowViewModel(def, RealmType.ParaMud, null, timers, () => { }, _ => { });

        row.RefreshStatus();                            // unmarked
        Assert.Equal("ALIVE", row.StatusDisplay);
        Assert.False(row.IsActive);

        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow);
        row.RefreshStatus();
        Assert.Equal("DEAD", row.StatusDisplay);
        Assert.True(row.IsActive);                      // Clear button shows while dead
    }
}
