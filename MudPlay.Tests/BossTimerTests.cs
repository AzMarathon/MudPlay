using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
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

    [Fact]
    public void EarlyWindows_Paradigm_ListsAllUnpassedWindowsSoonestFirst()
    {
        // full = 24h. Early points at 19.2 / 21.6 / 22.8h. Fresh timer → all three,
        // soonest first, each remaining = its point.
        var w = BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(0));
        Assert.Equal(new[] { "-20%", "-10%", "-5%" }, w.Select(x => x.Label).ToArray());
        Assert.Equal(new[] { 19.2, 21.6, 22.8 }, w.Select(x => Math.Round(x.Remaining.TotalHours, 1)).ToArray());
    }

    [Fact]
    public void EarlyWindows_DropOffAsTheyPass()
    {
        // Past -20% (19.2h): only -10% and -5% remain.
        var w = BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(20));
        Assert.Equal(new[] { "-10%", "-5%" }, w.Select(x => x.Label).ToArray());

        // Past every early point: none left (the full timer covers the rest).
        Assert.Empty(BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(23)));
    }

    [Fact]
    public void EarlyWindows_Stock_HasSingleWindow()
    {
        var w = BossTimerMath.EarlyWindows(RealmType.Stock, 24, TimeSpan.FromHours(0));
        Assert.Equal(new[] { "87.5%" }, w.Select(x => x.Label).ToArray());
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
        bosses.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        BossTimerStore timers = new(bosses, cache);
        timers.OnRealmChanged(AppPaths.GameDataSetDir(_set));
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

        // A fresh store for the same realm reloads the persisted kill time.
        BossStore bosses2 = new(seedPath: _seedPath); bosses2.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        GameDataCache cache2 = new(); cache2.SwitchSet(_set);
        BossTimerStore reloaded = new(bosses2, cache2);
        reloaded.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        Assert.NotNull(reloaded.KilledAt("ogre king"));
    }

    // Two characters on one realm, a client each, share the realm's timers file. Each
    // used to write the file from its own copy alone, so a kill on one client erased
    // the other's from the file, and after a restart a character had the other's
    // timers and none of its own.
    [Fact]
    public void TwoClientsOnOneRealm_KeepEachOthersTimers()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"), Boss("guardian golem", number: 51, rooms: "3/301"));
        var (_, first, _) = NewStores();
        var (_, second, _) = NewStores();

        first.MarkKilled("ogre king");
        second.MarkKilled("guardian golem");     // written on top of the first client's kill

        var (_, restarted, _) = NewStores();
        Assert.NotNull(restarted.KilledAt("ogre king"));
        Assert.NotNull(restarted.KilledAt("guardian golem"));

        // The first client picks the other's kill up on its next poll, and says so.
        int changed = 0;
        first.Changed += () => changed++;
        Assert.Null(first.KilledAt("guardian golem"));
        Assert.True(first.TakeInOutsideChanges());
        Assert.NotNull(first.KilledAt("guardian golem"));
        Assert.Equal(1, changed);
        Assert.False(first.TakeInOutsideChanges());   // nothing new since
    }

    [Fact]
    public void ResetOnOneClient_LeavesTheOtherClientsNewerKillInPlace()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"), Boss("guardian golem", number: 51, rooms: "3/301"));
        var (_, first, _) = NewStores();
        var (_, second, _) = NewStores();
        first.MarkKilled("ogre king");
        second.MarkKilled("guardian golem");

        first.Reset("ogre king");

        var (_, restarted, _) = NewStores();
        Assert.Null(restarted.KilledAt("ogre king"));
        Assert.NotNull(restarted.KilledAt("guardian golem"));
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
    public void OnMonsterDied_MultiWordNameWithWrapDoubledSpace_Matches()
    {
        // A multi-word monster name split across a word-wrap boundary in the death
        // line / roster comes back with a doubled internal space; the name match must
        // collapse it or a genuinely-killed boss never marks a timer (user report:
        // "colossal midnight dragon" auto-capture missed — same wrap-noise as @uses).
        SeedGameData(RealmType.ParaMud, ("colossal midnight dragon", 851, 24, 1));
        SeedBosses(Boss("colossal midnight dragon", number: 851, rooms: "17/1772"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(false, (null, "colossal  midnight dragon")), new RoomKey(17, 1772), engagedName: null);
        Assert.NotNull(timers.KilledAt("colossal midnight dragon"));
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

    private static RoomEntitiesObservation Roster(
        RoomObservationSource source, params string[] monsterNames) => new(
        "Also here: …",
        monsterNames.Select(n => new RoomEntity(n, n, EntityKind.Monster, null)).ToList(),
        DateTimeOffset.UtcNow,
        source);

    [Fact]
    public void RosterFallback_BossVanishesFromReparse_MarksKilled()
    {
        // The user's fallback: the boss is in the "Also here:" list, then a
        // same-room re-parse no longer shows it — a kill we never engaged.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room, "Spider Lair");
        Assert.Null(timers.KilledAt("giant spider"));   // still present, not yet a kill

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room, "Spider Lair");
        Assert.Null(timers.KilledAt("giant spider"));   // held until the display names the room
        timers.OnRoomDisplayed("Spider Lair");
        Assert.NotNull(timers.KilledAt("giant spider"));   // same room re-shown without it → killed
    }

    // Report paradigm-20260928-163621: "Also here:" arrives before "Obvious exits:", so
    // a room command out of the boss's room (`go manhole`) had the next room's roster
    // read against the boss's room while the tracker still held it.
    [Fact]
    public void RosterFallback_NextRoomsRoster_DoesNotMark()
    {
        SeedGameData(RealmType.ParaMud, ("mayor godfrey", 784, 2, 1));
        SeedBosses(Boss("mayor godfrey", number: 784, rooms: "1/2"));
        var (_, timers, _) = NewStores();
        RoomKey square = new(1, 2);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Mayor Godfrey"), square, "Town Square");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "large giant rat"), square, "Town Square");
        timers.OnRoomDisplayed("Sewer Tunnel, Junction");

        Assert.Null(timers.KilledAt("mayor godfrey"));
    }

    [Fact]
    public void RosterFallback_MoveInFlight_DoesNotMark()
    {
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room, "Spider Lair");
        // A same-named neighbour's roster while our step is still landing.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room, "Spider Lair", movePending: true);
        timers.OnRoomDisplayed("Spider Lair");

        Assert.Null(timers.KilledAt("giant spider"));
    }

    [Fact]
    public void RosterFallback_Departure_DoesNotMark()
    {
        // A boss that walks out (Departure) instead of dying must NOT start a timer.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room);
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.Departure), room);
        Assert.Null(timers.KilledAt("giant spider"));

        // A later AlsoHere without it must not resurrect the false kill either —
        // the Departure already dropped it from the present-set.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room);
        Assert.Null(timers.KilledAt("giant spider"));
    }

    [Fact]
    public void RosterFallback_LeftTheRoom_DoesNotMark()
    {
        // Absence after we changed rooms isn't a kill — the boss just isn't here.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();

        timers.OnRoomEntitiesObserved(
            Roster(RoomObservationSource.AlsoHere, "giant spider"), new RoomKey(1, 1678));
        // A re-parse in a DIFFERENT room (we walked out) — the boss's room no longer
        // matches, so nothing is marked.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), new RoomKey(1, 1679));
        Assert.Null(timers.KilledAt("giant spider"));
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
        // The broadcast channel opened, as with its Talk-tab box unticked.
        engine.DisableBroadcastChannel = false;
        engine.Now = () => _engineNow;
        _ = new BossTimerQueryHandler(engine, store, timers, cache);
        return (engine, timers);
    }

    // The engine's clock: it bounds what it says on the broadcast channel by the minute.
    private DateTimeOffset _engineNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

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

    // A timer sync may be asked for wherever players talk: said, telepathed, on the
    // gang channel or on the broadcast channel. It is answered on that channel, and
    // what is answered decodes back to the timer.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming, "/Bob {@timerdata ")]
    [InlineData(ChatChannel.Gangpath, "bg {@timerdata ")]
    [InlineData(ChatChannel.Local, ">Bob {@timerdata ")]
    [InlineData(ChatChannel.Broadcast, "br {@timerdata ")]
    public void TimerSync_OnEveryChannelItMayUse_IsAnsweredThere(ChatChannel channel, string opening)
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");

        engine.DispatchForTests(new ChatLogEntry(Now, channel, "Bob", "@timer sync", "raw"));

        string[] wire = engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToArray();
        Assert.NotEmpty(wire);
        Assert.All(wire, line => Assert.StartsWith(opening, line));
        string blob = string.Concat(Replies(engine).Select(r => r.Split(' ', 3)[2]));
        Assert.Equal(50, Assert.Single(BossTimerSyncCodec.Decode(blob)).MonsterNumber);
    }

    // Sneaking, the answer to a said request goes by telepath; the asker takes a
    // telepathed answer after a said request for that reason
    // (BossTimerSyncCollectorTests).
    [Fact]
    public void TimerSync_SaidToASneakingClient_IsAnsweredByTelepath()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.StealthedProvider = () => true;

        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, "Bob", "@timer sync", "raw"));

        Assert.All(engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)),
            line => Assert.StartsWith("/Bob {@timerdata ", line));
    }

    // On the broadcast channel an answer is several lines to everyone there, from
    // every client the asker may ask. The whole answer goes out, as one answer; a
    // second ask from the same player inside the minute draws nothing, and counts
    // for nothing against the other players there.
    [Fact]
    public void TimerSync_OnTheBroadcastChannel_GoesOutWhole_OnceAMinuteToEachAsker()
    {
        (string Name, int Number, int Regen)[] bosses =
            Enumerable.Range(1, 30).Select(i => ($"boss number {i}", 1000 + i, 24)).ToArray();
        var (engine, timers) = SetupHandler(RealmType.ParaMud, bosses);
        foreach ((string name, _, _) in bosses) timers.MarkKilled(name);
        ChatLogEntry Asks(string who, ChatChannel channel = ChatChannel.Broadcast) =>
            new(Now, channel, who, "@timer sync", "raw");
        string[] Sent()
        {
            string[] wire = engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToArray();
            engine.LastSentForTests.Clear();
            return wire;
        }

        // Every line of the set, in order, decoding to all thirty timers.
        void SentTheTimersWhole(string opening)
        {
            string[] wire = Sent();
            Assert.True(wire.Length >= 3, $"only {wire.Length} line(s): the answer must be several for this test");
            for (int i = 0; i < wire.Length; i++)
                Assert.StartsWith($"{opening}{{@timerdata {i + 1}/{wire.Length} ", wire[i]);
            string blob = string.Concat(wire.Select(StripWire).Select(r => r.Split(' ', 3)[2]));
            Assert.Equal(30, BossTimerSyncCodec.Decode(blob).Count);
        }

        engine.DispatchForTests(Asks("Bob"));
        SentTheTimersWhole("br ");

        _engineNow += TimeSpan.FromSeconds(59);
        engine.DispatchForTests(Asks("Bob"));
        Assert.Empty(Sent());

        // The several lines were one answer, and an answer to a player who holds the
        // permission: the refusals to those who don't are counted apart, so the
        // others on the channel still have all of theirs this minute, the channel's
        // few, and no more.
        for (int i = 1; i <= BroadcastAnswerBudget.AnswersPerWindow; i++)
        {
            engine.DispatchForTests(Asks($"Passerby{i}"));
            Assert.Equal("br {command invalid or not allowed}\r", Sent().Single());
        }
        engine.DispatchForTests(Asks("OneMore"));
        Assert.Empty(Sent());

        // The bound is on that channel alone: the same player is answered by telepath.
        engine.DispatchForTests(Asks("Bob", ChatChannel.TelepathIncoming));
        SentTheTimersWhole("/Bob ");

        // A minute after the answer, it is given again.
        _engineNow += TimeSpan.FromSeconds(1);
        engine.DispatchForTests(Asks("Bob"));
        SentTheTimersWhole("br ");
    }

    // The broadcast channel is off until the user opens it: nothing asked there is
    // read, timers included.
    [Fact]
    public void TimerSync_OnTheBroadcastChannelSwitchedOff_IsNotAnswered()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.DisableBroadcastChannel = true;

        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Broadcast, "Bob", "@timer sync", "raw"));

        Assert.Empty(engine.LastSentForTests);
    }

    // ----- the merge window: what is written without a pick -----------------

    private static readonly DateTimeOffset Killed = new(2026, 8, 3, 21, 30, 0, TimeSpan.Zero);

    // The merge window over real stores, tracking one boss it holds no timer for:
    // the case in which an offered timer is adopted outright when one player was
    // asked, and is a pick when several could answer.
    // askedOn: the channel the request that opened the window was typed on (null:
    // opened from the Bosses tab, nothing asked yet); to: who a telepathed one went to.
    // telepathWasUsers: whether a telepathed request of ours seen going out was the
    // user's own (null: every one is).
    private (BossTimerSyncViewModel window, BossTimerStore timers) NewSyncWindow(
        RemoteChannel? askedOn = RemoteChannel.Broadcast, string? to = null, Func<ChatLogEntry, bool>? telepathWasUsers = null)
    {
        // The golem is in the game data and not on our list.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "1/1"));
        var (bosses, timers, cache) = NewStores();
        _syncBosses = bosses;
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        _syncSent.Clear();
        BossTimerSyncViewModel window = new(bosses, timers, cache, new ChatRouter(router), send: _syncSent.Add,
            preArmedOn: askedOn, new LogService(), broadcastOff: () => false,
            isOurName: name => name == "Us", windowActive: _ => { }, preArmedTo: to, onUiThread: act => act(),
            telepathWasUsers: telepathWasUsers);
        return (window, timers);
    }

    // What the merge window put on the wire, and the boss list it works on.
    private readonly List<string> _syncSent = new();
    private BossStore _syncBosses = null!;   // set by NewSyncWindow

    // One responder's timer for the boss. alone: it is the answer of the one player
    // a telepath asked, with nothing asked where others listen.
    private static BossTimerSyncResponse Offers(
        string sender, RemoteChannel heardOn, bool alone = false, double hoursLater = 0) =>
        new(sender, new[] { new BossTimerSyncRecord(50, null, Killed.AddHours(hoursLater)) }, heardOn, alone);

    // The same timer as the chat lines the window's own collector reads, heard on a
    // channel from a player.
    private static void Hears(BossTimerSyncViewModel window, ChatChannel channel, string sender, double hoursLater = 0)
    {
        string blob = BossTimerSyncCodec.Encode(new[] { new BossTimerSyncRecord(50, null, Killed.AddHours(hoursLater)) });
        string line = $"{{{BossTimerQueryHandler.SyncResponseToken} 1/1 {blob}}}";
        window.CollectorForTests.Ingest(new ChatLogEntry(Now, channel, sender, line, line));
    }

    // Our own request seen going out, as the chat log shows it back to us.
    private static void SeesUsAsk(BossTimerSyncViewModel window, ChatChannel channel, string? speaker) =>
        window.CollectorForTests.Ingest(new ChatLogEntry(Now, channel, speaker, "@timer sync", "raw"));

    private static void Requests(BossTimerSyncViewModel window, int channelIndex, string target = "")
    {
        window.ChannelIndex = channelIndex;
        window.TelepathTarget = target;
        window.SendRequestCommand.Execute(null);
    }

    // Anyone can join the broadcast channel, and a request there tells every
    // listener that a collection is open. A timer a player with no standing sends
    // on it, for a boss we track and hold no timer for, is written nowhere: it is a
    // row to pick, naming who sent it and where it was heard, with "keep ours"
    // chosen. Closing the window leaves the timers as they were.
    [Fact]
    public void SyncWindow_ATimerHeardOnBroadcast_IsOfferedAsAPick_AndNothingIsWritten()
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;

            window.Fold(Offers("Stranger", RemoteChannel.Broadcast));

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasAutoMerged);
            Assert.True(row.KeepOursSelected);
            Assert.Null(row.SelectedKilledAt);
            BossTimerSyncCellViewModel offer = Assert.Single(row.Responders);
            Assert.Equal("Stranger", offer.Responder);
            Assert.False(offer.IsSelected);
            Assert.EndsWith("heard on broadcast", offer.Text);
            Assert.Contains("never adopted without a pick", window.Status);

            window.ApplyCommand.Execute(null);   // nothing picked

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.False(closedWithWrites);
        }
    }

    // Picked, the same row is merged like any other.
    [Fact]
    public void SyncWindow_APickedBroadcastRow_IsMergedOnApply()
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            window.Fold(Offers("Stranger", RemoteChannel.Broadcast));
            Assert.Null(timers.KilledAt("ogre king"));

            Assert.Single(Assert.Single(window.Rows).Responders).AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // Asked by telepath of one player, the same timer is adopted as it arrives, and
    // closing without Apply still reports the write.
    [Fact]
    public void SyncWindow_TheSameTimerAnsweringATelepath_IsAdoptedOutright()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: "Bob");
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            Assert.Contains("taken from the player you asked", window.Status);

            window.Fold(Offers("Bob", RemoteChannel.Telepath, alone: true));

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.True(Assert.Single(window.Rows).WasAutoMerged);
            Assert.Contains("0 to pick, 1 adopted", window.Status);
            Assert.DoesNotContain("broadcast", window.Status);

            window.CancelCommand.Execute(null);
            Assert.True(closedWithWrites);
        }
    }

    // Asked where several players may answer (gang, say), nothing is written on
    // arrival: the timer is a row to pick, with taking none chosen, and closing the
    // window leaves the timers as they were. A sneaking client's telepathed answer
    // to a said request is an answer to that request, and no different.
    [Theory]
    [InlineData(RemoteChannel.Gangpath, RemoteChannel.Gangpath)]
    [InlineData(RemoteChannel.Local, RemoteChannel.Local)]
    [InlineData(RemoteChannel.Local, RemoteChannel.Telepath)]
    public void SyncWindow_ATimerAnsweringAManyListenerRequest_IsAPick_AndNothingIsWritten(
        RemoteChannel askedOn, RemoteChannel heardOn)
    {
        var (window, timers) = NewSyncWindow(askedOn);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            Assert.Contains("nothing is written before that", window.Status);

            window.Fold(Offers("Bob", heardOn));

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasAutoMerged);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Take none", row.KeepOursLabel);
            Assert.Null(row.SelectedKilledAt);
            Assert.DoesNotContain("heard on broadcast", Assert.Single(row.Responders).Text);
            Assert.Contains("1 to pick, 0 adopted, 0 already in sync", window.Status);

            window.CancelCommand.Execute(null);

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.False(closedWithWrites);
        }
    }

    // Several responders, each with a different timer for a boss we hold none for:
    // one row, one pick apiece, none taken until one is chosen. Apply writes the
    // chosen one and no other; choosing "take none" again writes nothing.
    [Fact]
    public void SyncWindow_SeveralRespondersForOneBoss_TheUserPicksWhichOrNone()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;

            window.Fold(Offers("Bob", RemoteChannel.Gangpath));
            window.Fold(Offers("Sue", RemoteChannel.Gangpath, hoursLater: 3));

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
            Assert.Contains("2 responder(s): 1 to pick, 0 adopted", window.Status);

            row.Responders[0].AdoptCommand.Execute(null);
            row.KeepOursCommand.Execute(null);
            Assert.Null(row.SelectedKilledAt);
            Assert.All(row.Responders, c => Assert.False(c.IsSelected));

            row.Responders[1].AdoptCommand.Execute(null);
            Assert.Null(timers.KilledAt("ogre king"));
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
            Assert.True(closedWithWrites);
        }
    }

    // What writes nothing on arrival is the same wherever it was asked: a timer
    // matching ours is left alone, and one that disagrees is a pick beside ours.
    [Theory]
    [InlineData(RemoteChannel.Gangpath)]
    [InlineData(RemoteChannel.Local)]
    [InlineData(RemoteChannel.Broadcast)]
    [InlineData(RemoteChannel.Telepath)]
    public void SyncWindow_InSyncAndConflictingTimers_AreAsBeforeOnEveryChannel(RemoteChannel askedOn)
    {
        var (window, timers) = NewSyncWindow(askedOn);
        using (window)
        {
            timers.MarkKilled("ogre king", Killed);

            window.Fold(Offers("Bob", askedOn, hoursLater: 20.0 / 3600));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.HasConflict);
            Assert.Contains("in sync with Bob", row.ResolvedStatus);
            Assert.Contains("0 to pick, 0 adopted, 1 already in sync", window.Status);

            window.Fold(Offers("Sue", askedOn, hoursLater: 3));
            Assert.True(row.HasConflict);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.Equal("Sue", Assert.Single(row.Responders).Responder);
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // ----- the merge window: who a timer is adopted from ---------------------

    // Asked by telepath: the player the game says the telepath reached is the one
    // asked, and their telepathed timer is taken as it arrives. The name typed may
    // be the first letters of theirs, so it is nobody's name by itself: a player
    // who happens to be called that is not adopted from, and neither is anyone
    // else who telepaths.
    [Fact]
    public void SyncWindow_AskedByTelepath_AdoptsOnlyFromThePlayerTheGameSaysItReached()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "bo");
            Assert.Equal("/bo @timer sync", _syncSent.Single());
            SeesUsAsk(window, ChatChannel.TelepathOutgoing, "Bob");   // --- Telepath Sent to Bob ---

            Hears(window, ChatChannel.TelepathIncoming, "Bo", hoursLater: 2);
            Hears(window, ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Bo", "Sue" }, Assert.Single(window.Rows).Responders.Select(c => c.Responder));
            Assert.Contains("1 to pick, 0 adopted", window.Status);

            Hears(window, ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            // The row still holds the other players' picks, and counts as adopted too.
            Assert.Contains("3 responder(s): 1 to pick, 1 adopted", window.Status);
        }
    }

    // No echo for the telepath (its letters fitted two players, or nobody, and it
    // reached no one; or the echo was missed): nobody is known to have been asked,
    // so nothing adopts, even from a player whose name is exactly what was typed.
    [Fact]
    public void SyncWindow_ATelepathTheGameNeverShowedSent_AdoptsFromNobody()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");

            Hears(window, ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Bob", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
            Assert.Contains("1 to pick, 0 adopted", window.Status);
        }
    }

    // A telepath goes to one word. Two would send the second as part of the
    // message to whoever the first names: nothing is sent.
    [Theory]
    [InlineData("Sir Bob")]
    [InlineData("Bob\tSmith")]
    public void SyncWindow_ATelepathTargetOfTwoWords_IsNotSent(string target)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, target);

            Assert.Empty(_syncSent);
            Assert.False(window.Requested);
            Assert.StartsWith("A telepath goes to one name", window.Status);
            Hears(window, ChatChannel.TelepathIncoming, "Sir");
            Assert.Empty(window.Rows);
            Assert.Null(timers.KilledAt("ogre king"));
        }
    }

    // Adopting is for a request the user made. A `@timer sync` our client telepathed
    // for another player (their relay-back, or a `@do` of theirs) opens the window
    // and is listened to, and that player's answer is a pick like anyone's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncWindow_ATelepathTheClientSentForAnotherPlayer_AdoptsNothing(bool windowAlreadyOpen)
    {
        var (window, timers) = windowAlreadyOpen
            ? NewSyncWindow(askedOn: null, telepathWasUsers: echo => echo.Speaker != "Friend")
            // Opened by the echo: the app passes no player for one that was not the user's.
            : NewSyncWindow(RemoteChannel.Telepath, to: null);
        using (window)
        {
            if (windowAlreadyOpen)
            {
                // The user asked Bob; then the client telepathed Friend for Friend.
                Requests(window, 1, "Bob");
                SeesUsAsk(window, ChatChannel.TelepathOutgoing, "Bob");
                SeesUsAsk(window, ChatChannel.TelepathOutgoing, "Friend");
            }

            Hears(window, ChatChannel.TelepathIncoming, "Friend", hoursLater: 3);

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Friend", Assert.Single(Assert.Single(window.Rows).Responders).Responder);

            // The player the user did ask is still adopted from.
            if (!windowAlreadyOpen) return;
            Hears(window, ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // ----- the merge window: a boss that was not on our list -----------------

    private static BossTimerSyncResponse OffersBoth(string sender, RemoteChannel heardOn) => new(sender, new[]
    {
        new BossTimerSyncRecord(50, null, Killed),
        new BossTimerSyncRecord(51, "guardian golem", Killed),
    }, heardOn, false);

    private static BossTimerSyncRowViewModel RowOf(BossTimerSyncViewModel window, string boss) =>
        window.Rows.Single(r => r.BossName == boss);

    // A pick for a boss off our list is spent once it is written: the boss is on
    // the list now and the row shows the timer as ours, so a second Apply does not
    // write the offered time again over a kill made since.
    [Fact]
    public void SyncWindow_AnAdoptedBossIsNotWrittenAgainByASecondApply()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            window.Fold(OffersBoth("Bob", RemoteChannel.Gangpath));
            foreach (BossTimerSyncRowViewModel row in window.Rows) row.Responders[0].AdoptCommand.Execute(null);

            // Our own kill of the tracked boss keeps the window open after Apply.
            timers.MarkKilled("ogre king", Killed.AddHours(6));
            window.ApplyCommand.Execute(null);

            Assert.Contains("Applied 1 timer update(s). Not written: ogre king", window.Status);
            Assert.Equal(0, closes);
            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
            BossTimerSyncRowViewModel golem = RowOf(window, "guardian golem");
            Assert.Equal("guardian golem", golem.MatchName);
            Assert.Equal(Killed, golem.OursKilledAt);
            Assert.Equal("Keep ours", golem.KeepOursLabel);
            Assert.Null(golem.SelectedKilledAt);
            Assert.False(golem.Responders[0].IsSelected);

            DateTimeOffset fresh = Killed.AddHours(8);
            timers.MarkKilled("guardian golem", fresh);
            window.ApplyCommand.Execute(null);

            Assert.Equal(fresh, timers.KilledAt("guardian golem"));
            Assert.Equal(Killed.AddHours(6), timers.KilledAt("ogre king"));
            Assert.Equal("Applied 0 timer update(s).", window.Status);
            Assert.Equal(1, closes);
        }
    }

    // The same guard for a row built while the boss was off our list: we add the
    // boss and kill it while the window is open, and the pick, an older time, is
    // not written over that kill.
    [Fact]
    public void SyncWindow_APickForABossAddedSinceIsNotWrittenOverOurKillOfIt()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            window.Fold(OffersBoth("Bob", RemoteChannel.Gangpath));
            BossTimerSyncRowViewModel golem = RowOf(window, "guardian golem");
            Assert.Null(golem.MatchName);

            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());
            DateTimeOffset fresh = Killed.AddHours(8);
            timers.MarkKilled("guardian golem", fresh);
            golem.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(fresh, timers.KilledAt("guardian golem"));
            Assert.Contains("Not written: guardian golem", window.Status);
            Assert.Equal(0, closes);
            Assert.Equal("guardian golem", golem.MatchName);
            Assert.Equal(fresh, golem.OursKilledAt);
            Assert.Null(golem.SelectedKilledAt);

            // A later offer for it lands on the same row, weighed against our kill.
            window.Fold(new BossTimerSyncResponse("Sue",
                new[] { new BossTimerSyncRecord(51, "guardian golem", Killed.AddHours(3)) }, RemoteChannel.Gangpath, false));
            Assert.Equal(2, window.Rows.Count);
            Assert.Equal(new[] { "Bob", "Sue" }, golem.Responders.Select(c => c.Responder));
        }
    }

    // Added to our list with no timer yet, the boss is a tracked one we hold none
    // for: the pick is written, as the user chose.
    [Fact]
    public void SyncWindow_APickForABossAddedSinceWithNoTimerIsWritten()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            window.Fold(OffersBoth("Bob", RemoteChannel.Gangpath));
            BossTimerSyncRowViewModel golem = RowOf(window, "guardian golem");
            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());

            golem.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
        }
    }

    // A telepath with nobody named is not sent, and nothing is collected for it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SyncWindow_ATelepathWithNoName_IsNotSent(string target)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, target);

            Assert.Empty(_syncSent);
            Assert.False(window.Requested);
            Assert.Equal("Enter a player name to telepath, then request again.", window.Status);
            Hears(window, ChatChannel.TelepathIncoming, "Bob");
            Assert.Empty(window.Rows);
            Assert.Null(timers.KilledAt("ogre king"));
        }
    }

    // Once a request has gone where several players listen, in this window and in
    // either order, nothing is written on arrival: a telepath may be a sneaking
    // client's answer to the say, from the player asked as from anyone.
    [Theory]
    [InlineData(2, 1)]   // say, then telepath
    [InlineData(1, 2)]   // telepath, then say
    [InlineData(0, 1)]   // gang, then telepath
    [InlineData(1, 3)]   // telepath, then broadcast
    public void SyncWindow_OnceASharedChannelWasAsked_ATelepathedTimerIsAPick(int first, int second)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, first, "Bob");
            Requests(window, second, "Bob");
            Assert.Equal(2, _syncSent.Count);
            SeesUsAsk(window, ChatChannel.TelepathOutgoing, "Bob");   // the game shows the telepath reached Bob

            Hears(window, ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Hears(window, ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Sue", "Bob" }, Assert.Single(window.Rows).Responders.Select(c => c.Responder));
            Assert.Contains("1 to pick, 0 adopted", window.Status);
        }
    }

    // A second request typed by hand is the same as one made with the button: a
    // gang request typed while a telepath is being collected ends adopting.
    [Fact]
    public void SyncWindow_ASharedRequestTypedByHand_EndsAdoptingToo()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");
            SeesUsAsk(window, ChatChannel.TelepathOutgoing, "Bob");
            SeesUsAsk(window, ChatChannel.Gangpath, "Us");

            Hears(window, ChatChannel.Gangpath, "Sue", hoursLater: 3);
            Hears(window, ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(2, Assert.Single(window.Rows).Responders.Count);
        }
    }

    // The window opened by a telepath typed at the terminal knows who it went to.
    [Fact]
    public void SyncWindow_OpenedByATypedTelepath_AdoptsFromThePlayerItWentTo()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: "Bob");
        using (window)
        {
            Hears(window, ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Null(timers.KilledAt("ogre king"));

            Hears(window, ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // A second request by the button adds its channel: answers to the first are
    // still read, and a player who answered the first may answer the second.
    [Fact]
    public void SyncWindow_ASecondRequestByTheButton_AddsItsChannel()
    {
        var (window, _) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 0);
            Hears(window, ChatChannel.Gangpath, "Bob");
            Requests(window, 2);

            Hears(window, ChatChannel.Gangpath, "Sue", hoursLater: 3);   // a late answer to the first
            Hears(window, ChatChannel.Local, "Bob", hoursLater: 5);      // Bob again, to the second

            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal(new[] { "Sue", "Bob" }, row.Responders.Select(c => c.Responder));
            Assert.Equal(Killed.AddHours(5), row.Responders[1].KilledAt);
        }
    }

    // ----- the merge window: a timer of ours that moved while it was open ----

    // A pick is made against the timer the row showed as ours. We kill the boss
    // while the window is open; the pick, an older time, is not written over that
    // kill. The row is shown against our timer as it is now and the window stays
    // open. Picked again, it is written.
    [Fact]
    public void SyncWindow_APickIsNotWrittenOverATimerOfOursThatChangedSince()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            window.Fold(Offers("Bob", RemoteChannel.Gangpath));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            row.Responders[0].AdoptCommand.Execute(null);

            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);
            window.ApplyCommand.Execute(null);

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            Assert.Equal(0, closes);
            Assert.Contains("Applied 0 timer update(s). Not written: ogre king", window.Status);
            Assert.Equal(ownKill, row.OursKilledAt);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.True(row.KeepOursSelected);
            Assert.Null(row.SelectedKilledAt);

            // Picked again, against our timer as it now is, the pick is written.
            row.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal(1, closes);
        }
    }

    // The same on arrival: the asked player's timer is weighed against what we hold
    // when it comes, not against the row as it was built.
    [Fact]
    public void SyncWindow_AnArrivingTimerIsNotAdoptedOverAKillOfOursMadeSinceTheRowWasBuilt()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: "Bob");
        using (window)
        {
            Hears(window, ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);   // builds the row: we hold none
            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);

            Hears(window, ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.Equal(ownKill, row.OursKilledAt);
            Assert.Equal(new[] { "Sue", "Bob" }, row.Responders.Select(c => c.Responder));
        }
    }

    // The readable report is an ordinary remote command, and with the broadcast
    // channel opened those are read there too: it is answered on the channel, once
    // a minute to each asker like everything said there.
    [Fact]
    public void TimerReport_OnTheBroadcastChannel_IsAnsweredThere_OnceAMinute()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");

        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Broadcast, "Bob", "@timer", "raw"));
        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Broadcast, "Bob", "@timer ogre", "raw"));

        Assert.StartsWith("br {ogre king - ", Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests)));

        _engineNow += BroadcastAnswerBudget.Window;
        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Broadcast, "Bob", "@timer ogre", "raw"));
        Assert.Equal(2, engine.LastSentForTests.Count);
    }

    // With the box ticked the report is not read there, like everything else.
    [Fact]
    public void TimerReport_OnTheBroadcastChannelSwitchedOff_IsNotAnswered()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.DisableBroadcastChannel = true;

        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Broadcast, "Bob", "@timer", "raw"));

        Assert.Empty(engine.LastSentForTests);
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

    // The Darken Beast Lord sits in a den too dark to list what is in it, and its
    // death line isn't on record, so nothing named the kill and no timer started.
    // Its name in the fight's damage lines says it is there. It is worth 4,000,000
    // exp against 17,750 for the biggest thing that shares its den, so even split six
    // ways its kill is the only gain that size: the exp line marks it.
    private (BossTimerStore Timers, Func<int, MonsterDeathEvent> Gain) DarkDen(long bossExp = 4_000_000, params long[] others)
    {
        if (others.Length == 0) others = new long[] { 17_750, 14_750 };
        SeedGameData(RealmType.ParaMud, ("darken beast lord", 930, 15, 1));
        SeedBosses(Boss("darken beast lord", number: 930,
            rooms: new[] { "17/2812", "17/2813", "17/2814", "17/2815" }));
        var (_, timers, _) = NewStores();
        timers.SetRoomExpResolver((_, _) => (bossExp, others));
        return (timers, exp => new MonsterDeathEvent(
            Array.Empty<MonsterDeathIdentity>(), ExperienceGained: exp, At: DateTimeOffset.UtcNow, IsFallback: true));
    }

    // It has four rooms, and the kill counts in any of them; a full party's share
    // (a sixth) is still far more than an add gives.
    [Theory]
    [InlineData(2812)]
    [InlineData(2813)]
    [InlineData(2814)]
    [InlineData(2815)]
    public void UnnamedDeathInTheDark_WithABossSizedExpGain_MarksTheBoss(int room)
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(666_666), new RoomKey(17, room), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.NotNull(timers.KilledAt("darken beast lord"));
    }

    // An add dying in the same fight gives an add's exp: not the boss.
    [Fact]
    public void UnnamedDeathInTheDark_WithAnAddSizedExpGain_DoesNotMarkTheBoss()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(17_750), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // The boss has to have been in the fight's lines: a big gain alone marks nothing.
    [Fact]
    public void UnnamedDeath_WithTheBossNeverNamed_MarksNothing()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "darken beast" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // Outside its rooms the same lines mark nothing.
    [Fact]
    public void UnnamedDeath_OutsideTheBosssRooms_MarksNothing()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2811), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // The rule fits a boss worth far less than its neighbours as well: a monkey spirit
    // pays 40,000 among monsters worth 400,000, whose smallest share is 66,666.
    [Theory]
    [InlineData(40_000, true)]      // the spirit, solo
    [InlineData(6_667, true)]       // the spirit, shared six ways
    [InlineData(400_000, false)]    // one of the others
    [InlineData(66_667, false)]     // one of the others, shared six ways
    public void UnnamedDeath_OfABossWorthLessThanItsNeighbours_IsToldByWhatItPays(int gained, bool isBoss)
    {
        var (timers, gain) = DarkDen(bossExp: 40_000, 400_000);

        timers.OnMonsterDied(gain(gained), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.Equal(isBoss, timers.KilledAt("darken beast lord") is not null);
    }

    // Where the boss and an add could both have paid the gain, exp can't say: then it
    // is the boss's only when nothing else was named.
    [Fact]
    public void UnnamedDeath_WhereExpCantTell_NeedsTheBossToBeTheOnlyOneNamed()
    {
        var (timers, gain) = DarkDen(bossExp: 60_000, 17_750);

        // 12,000 is a fifth of the boss, and also within what the add pays.
        timers.OnMonsterDied(gain(12_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });
        Assert.Null(timers.KilledAt("darken beast lord"));

        timers.OnMonsterDied(gain(12_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord" });
        Assert.NotNull(timers.KilledAt("darken beast lord"));
    }

    // With the dead monster named some other way, the damage lines decide nothing.
    [Fact]
    public void NamedDeathOfAnAdd_IsNotTakenForTheBoss()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2812), engagedName: "shadow wolf",
            recentFoes: new[] { "Darken Beast Lord" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // ----- Bosses the game gives one name ------------------------------------

    private static BossDef Named(string name, string gameName, int number, string room) => new()
    {
        Name = name, GameName = gameName, MonsterNumber = number, Rooms = new() { room },
        InStock = true, InParadigm = true, RespawnType = BossRespawnType.Timed,
    };

    // The Nahr in the throne room and the one in the pit are two bosses with one name
    // and two timers. Read by name, both took the first Nahr record's hours.
    [Fact]
    public void TwoBossesOfOneGameName_EachReadTheirOwnRecordsTimer()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1004, 3, 1), ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (bosses, _, cache) = NewStores();

        Assert.Equal(2, bosses.Resolve().Count);
        Assert.Equal(10, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().First(b => b.Name == "nahr (spheres)")));
        Assert.Equal(4, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().First(b => b.Name == "nahr (spaceghost)")));
    }

    [Fact]
    public void TwoBossesOfOneGameName_AreToldApartByTheRoomOfTheKill()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(12, 2378), engagedName: "Nahr");

        Assert.NotNull(timers.KilledAt("nahr (spaceghost)"));
        Assert.Null(timers.KilledAt("nahr (spheres)"));
    }

    [Fact]
    public void RosterFallback_MarksTheBossOfThatRoom_WhenTwoShareAGameName()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (_, timers, _) = NewStores();
        RoomKey throne = new(17, 3087);
        List<string> killed = new();
        timers.BossKilled += def => killed.Add(def.Name);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Nahr"), throne, "Vaulted Throne Room");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), throne, "Vaulted Throne Room");
        timers.OnRoomDisplayed("Vaulted Throne Room");

        Assert.NotNull(timers.KilledAt("nahr (spheres)"));
        Assert.Null(timers.KilledAt("nahr (spaceghost)"));
        Assert.Equal(new[] { "nahr (spheres)" }, killed);
    }

    // A boss with one name can still have several records: the great green dragon of
    // the cavern is 15 hours, and an earlier record of the same name is 1.
    [Fact]
    public void RegenHours_AreReadFromTheBosssOwnRecord_NotTheFirstOfItsName()
    {
        SeedGameData(RealmType.ParaMud, ("great green dragon", 1038, 1, 1), ("great green dragon", 1058, 15, 1));
        var (_, _, cache) = NewStores();

        Assert.Equal(15, BossCatalog.ResolveRegenHours(cache, Boss("great green dragon", number: 1058, rooms: "1/3111")));
        Assert.Equal(1, BossCatalog.ResolveRegenHours(cache, Boss("great green dragon", number: null, rooms: "1/3111")));
    }

    // In other game data the number may be some other monster, or one that isn't a
    // boss: the name is what's left to go on.
    [Fact]
    public void RegenHours_FallBackToTheName_WhenTheNumberIsAnotherMonster()
    {
        SeedGameData(RealmType.ParaMud, ("giant rat", 792, 0, 0), ("giant roc", 571, 8, 1));
        var (_, _, cache) = NewStores();

        Assert.Equal(8, BossCatalog.ResolveRegenHours(cache, Boss("giant roc", number: 792, rooms: "10/159")));
    }

    // Malformation comes only from Lord Chisholm's death, so the two are one entry on
    // his record: the hour runs from his death, and the malformation dying afterwards
    // leaves it where it was (user, 2026-10-07).
    [Fact]
    public void BossThatDiesIntoAnother_IsTimedFromItsOwnDeath()
    {
        SeedGameData(RealmType.ParaMud, ("Lord Chisholm", 1297, 1, 1), ("malformation", 1298, 0, 1));
        SeedBosses(Named("lord chisholm (malformation)", "lord chisholm", 1297, "14/6216"));
        var (bosses, timers, cache) = NewStores();
        RoomKey chambers = new(14, 6216);

        timers.OnMonsterDied(Death(fallback: true), chambers, engagedName: "Lord Chisholm");
        DateTimeOffset? atHisDeath = timers.KilledAt("lord chisholm (malformation)");
        Assert.NotNull(atHisDeath);
        Assert.Equal(1, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().Single()));

        timers.MarkKilled("lord chisholm (malformation)", DateTimeOffset.UtcNow.AddMinutes(-10));
        DateTimeOffset? before = timers.KilledAt("lord chisholm (malformation)");
        timers.OnMonsterDied(Death(fallback: true), chambers, engagedName: "malformation");
        Assert.Equal(before, timers.KilledAt("lord chisholm (malformation)"));
    }

    // ----- What a boss's death summons ---------------------------------------

    private (BossTimerStore timers, List<(string Boss, string? Dead, int? Exp)> loot, List<string> kills) ChisholmsChambers()
    {
        SeedGameData(RealmType.ParaMud, ("Lord Chisholm", 1297, 1, 1), ("malformation", 1298, 0, 1));
        SeedBosses(Named("lord chisholm (malformation)", "lord chisholm", 1297, "14/6216"));
        var (_, timers, _) = NewStores();
        timers.SetDeathSummonResolver(def => def.MonsterNumber == 1297 ? new[] { "malformation" } : Array.Empty<string>());
        List<(string, string?, int?)> loot = new();
        List<string> kills = new();
        timers.BossLootDropped += (def, dead, exp) => loot.Add((def.Name, dead, exp));
        timers.BossKilled += def => kills.Add(def.Name);
        return (timers, loot, kills);
    }

    // The malformation is where Lord Chisholm's loot is. Its death is not his: it
    // starts no timer and is no boss kill, but it is when the drops land.
    [Fact]
    public void DeathOfWhatABossSummons_DropsLoot_WithoutBeingAKill()
    {
        var (timers, loot, kills) = ChisholmsChambers();
        RoomKey chambers = new(14, 6216);

        timers.OnMonsterDied(Death(fallback: true), chambers, engagedName: "Lord Chisholm");
        Assert.Equal(new[] { "lord chisholm (malformation)" }, kills);
        Assert.Equal(("lord chisholm (malformation)", "Lord Chisholm", 100), Assert.Single(loot));

        timers.OnMonsterDied(Death(fallback: true), chambers, engagedName: "malformation");
        Assert.Single(kills);
        Assert.Equal(2, loot.Count);
        Assert.Equal(("lord chisholm (malformation)", "malformation", 100), loot[1]);
    }

    [Fact]
    public void DeathOfWhatABossSummons_OutsideItsRooms_IsNothing()
    {
        var (timers, loot, _) = ChisholmsChambers();

        timers.OnMonsterDied(Death(fallback: true), new RoomKey(14, 1), engagedName: "malformation");

        Assert.Empty(loot);
    }

    // The boss gone from its room without a death seen still drops: nothing is
    // known of the dead monster.
    [Fact]
    public void RosterFallback_DropsLoot_WithNothingKnownOfTheDeath()
    {
        var (timers, loot, _) = ChisholmsChambers();
        RoomKey chambers = new(14, 6216);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Lord Chisholm"), chambers, "Lord's Chambers");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), chambers, "Lord's Chambers");
        timers.OnRoomDisplayed("Lord's Chambers");

        Assert.Equal(("lord chisholm (malformation)", (string?)null, (int?)null), Assert.Single(loot));
    }
}
