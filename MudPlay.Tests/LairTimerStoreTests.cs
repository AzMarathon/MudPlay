using System.IO;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 7.18 — LairTimerStore default-respawn lookup paths. We exercise
/// both shapes the MDB exports: the NMR 1.83+ <c>Lairs.AvgDelay</c>
/// path (resolved via the GroupIndex back-reference on the room's
/// lair tag) and the pre-1.83 fallback that picks the slowest
/// <c>Monsters.RegenTime</c> across the listed mob ids.
/// </summary>
/// <remarks>
/// In-session arrival tracking is wired through
/// <see cref="RoomTracker.StateChanged"/> — that path is exercised by
/// the integration smoke once the scheduler PR (7.19) lands; this
/// suite focuses on the deterministic JSON-only lookup, the
/// game-data-cache invalidation contract, and the <c>NextReadyAt</c>
/// projection.
/// </remarks>
public sealed class LairTimerStoreTests : IDisposable
{
    private readonly string _setName;

    public LairTimerStoreTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-lairtimer-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- fixture builders -----------------------------------------

    // One lair room at 5/100 keyed to GroupIndex "120-005-001"; one
    // non-lair room at 1/1 for the empty-tag negative test.
    private const string RoomsJson = """
        [
          { "Map Number": 5, "Room Number": 100, "Name": "Sewer Lair",
            "Light": 0, "Shop": 0, "Lair": "[120-005-001][2]Group(lair): 5/100", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1, "Name": "Lobby",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 50, "Name": "Old Lair",
            "Light": 0, "Shop": 0, "Lair": "12, 34, 56", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [
          { "GroupIndex": "120-005-001", "AvgDelay": 30 }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 12, "RegenTime": 5 },
          { "Number": 34, "RegenTime": 15 },
          { "Number": 56, "RegenTime": 10 }
        ]
        """;

    // Info.Legit 2 marks a Paradigm set; no Info table reads as Stock.
    private const string ParadigmInfoJson = """[ { "Legit": 2 } ]""";

    private (GameDataCache cache, RoomGraphManager graph, RoomTracker tracker)
        BuildFixture(string? lairsOverride = null, string? monstersOverride = null,
            RealmType realm = RealmType.Stock)
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    RoomsJson);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    lairsOverride    ?? LairsJson);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monstersOverride ?? MonstersJson);
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        return (cache, graph, tracker);
    }

    // ----- DefaultRespawnSeconds — per-room Delay (primary) -------

    [Theory]
    [InlineData(RealmType.ParaMud, 270)]   // (5-1)*60 + 30
    [InlineData(RealmType.Stock, 330)]     // 5 to 6 min after the last kill → 5*60 + 30
    public void DefaultRespawnSeconds_PerRoomDelay_TakesPrecedence(RealmType realm, int expected)
    {
        // Rooms.json with Delay=5, read by realm. Lairs.json says 30 min
        // (1800s) for the same group index — the per-room Delay must win
        // because that's the tooltip's source of truth + the actual
        // server behaviour.
        const string roomsWithDelay = """
            [
              { "Map Number": 1, "Room Number": 35, "Name": "Intersection",
                "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/35", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), roomsWithDelay);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),
            """[ { "GroupIndex": "1-1-1", "AvgDelay": 30 } ]""");
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(expected, store.DefaultRespawnSeconds(new RoomKey(1, 35)));
    }

    [Theory]
    [InlineData(RealmType.ParaMud, 30)]   // (1-1)*60 + 30
    [InlineData(RealmType.Stock, 90)]     // 1 to 2 min → 1*60 + 30
    public void DefaultRespawnSeconds_PerRoomDelayOne(RealmType realm, int expected)
    {
        // Edge of the formula: Delay=1.
        const string roomsJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "FastRespawn",
                "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/1", "Delay": 1,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    roomsJson);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    "[]");
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), "[]");
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(expected, store.DefaultRespawnSeconds(new RoomKey(1, 1)));
    }

    // ----- DefaultRespawnSeconds — NMR 1.83+ path -------------------

    [Fact]
    public void DefaultRespawnSeconds_NmrTag_ResolvesViaGroupIndexAvgDelay()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        int? seconds = store.DefaultRespawnSeconds(new RoomKey(5, 100));

        // AvgDelay = 30 minutes × 60s/min = 1800s under the stock-realm
        // unit conversion (see LairTimerStore.AvgDelayUnitSeconds).
        Assert.Equal(1800, seconds);
    }

    [Fact]
    public void DefaultRespawnSeconds_NonLairRoom_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(1, 1)));
    }

    [Fact]
    public void DefaultRespawnSeconds_UnknownRoom_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(999, 999)));
    }

    // ----- DefaultRespawnSeconds — pre-1.83 fallback ----------------

    [Fact]
    public void DefaultRespawnSeconds_PreNmrTag_ResolvesToSlowestMonsterRegen()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        int? seconds = store.DefaultRespawnSeconds(new RoomKey(7, 50));

        // Slowest of {5, 15, 10} = 15 minutes × 60s/min = 900s.
        Assert.Equal(900, seconds);
    }

    [Fact]
    public void DefaultRespawnSeconds_PreNmrTag_NoMatchingMonsters_ReturnsNull()
    {
        // Monsters table missing the listed ids — we can't resolve a
        // respawn, return null so the scheduler treats it as "ready
        // now" rather than guessing.
        var (cache, graph, tracker) = BuildFixture(monstersOverride: "[]");
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(7, 50)));
    }

    // ----- Cache invalidation ---------------------------------------

    [Fact]
    public void ActiveSetChanged_DropsCachedRespawnLookups()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // Prime the cache then rewrite Lairs.json with a different
        // delay and switch sets — the new value should be observed.
        Assert.Equal(1800, store.DefaultRespawnSeconds(new RoomKey(5, 100)));

        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),
            """[ { "GroupIndex": "120-005-001", "AvgDelay": 90 } ]""");
        // Swap to null then back — cleanest way to force the cache to
        // re-read without poking private state.
        cache.SwitchSet(null);
        cache.SwitchSet(_setName);
        graph.OnActiveSetChanged(_setName);

        Assert.Equal(5400, store.DefaultRespawnSeconds(new RoomKey(5, 100)));
    }

    // ----- MaxDefaultRespawnSeconds ---------------------------------

    [Fact]
    public void MaxDefaultRespawnSeconds_ReturnsLongestLairInSet()
    {
        // Fixture has two lairs: 5/100 (AvgDelay 30min = 1800s) and 7/50
        // (slowest monster regen 15min = 900s), plus non-lair 1/1. The
        // whole-set max is the 1800s lair.
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(1800, store.MaxDefaultRespawnSeconds());
    }

    [Fact]
    public void MaxDefaultRespawnSeconds_NoLairs_ReturnsNull()
    {
        // A set with only non-lair rooms resolves no respawn at all.
        const string roomsNoLair = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Lobby",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    roomsNoLair);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    "[]");
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), "[]");

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.MaxDefaultRespawnSeconds());
    }

    // ----- NextReadyAt / LastEntered no-arrival path ----------------

    [Fact]
    public void NextReadyAt_NoArrival_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // No arrival recorded → no anchor for the projection.
        // Scheduler treats null as "ready now" per the API contract.
        Assert.Null(store.NextReadyAt(new RoomKey(5, 100)));
        Assert.Null(store.LastEntered(new RoomKey(5, 100)));
    }

    [Fact]
    public void NextReadyAt_NoRespawnAndOverrideNull_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // Non-lair room — no respawn timer available at all.
        Assert.Null(store.NextReadyAt(new RoomKey(1, 1)));
    }

    // ----- Clock start — Stock last kill vs Paradigm entry ----------

    [Fact]
    public void Stock_ClockRunsFromTheLastKill_NotTheEntry()
    {
        // Stock restarts one room clock on every kill, so a fight that ran after
        // the entry pushes the ready-time out by the fight's length.
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        DateTimeOffset entered = store.LastEntered(lair)!.Value;

        DateTimeOffset firstKill = entered.AddSeconds(10);
        DateTimeOffset lastKill = entered.AddSeconds(25);
        store.NoteKill(firstKill);
        store.NoteKill(lastKill);

        Assert.Equal(lastKill, store.ClockStart(lair));
        Assert.Equal(lastKill.AddSeconds(1800), store.NextReadyAt(lair));
        Assert.Equal(lastKill.AddSeconds(60), store.NextReadyAt(lair, overrideRespawnSeconds: 60));
    }

    [Fact]
    public void Stock_NoKillSeen_FallsBackToTheEntry()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);

        Assert.Equal(store.LastEntered(lair), store.ClockStart(lair));
    }

    [Fact]
    public void Paradigm_ClockStillRunsFromTheEntry()
    {
        // Paradigm's clock is unsettled (issue #813); it keeps timing from the entry.
        var (cache, graph, tracker) = BuildFixture(realm: RealmType.ParaMud);
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        DateTimeOffset entered = store.LastEntered(lair)!.Value;

        store.NoteKill(entered.AddSeconds(25));

        Assert.Equal(entered, store.ClockStart(lair));
        Assert.Equal(entered.AddSeconds(1800), store.NextReadyAt(lair));
    }

    [Fact]
    public void NoteKill_OutsideALairRoom_IsIgnored()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        tracker.SetLocated(new RoomKey(1, 1));   // the non-lair lobby

        store.NoteKill(DateTimeOffset.UtcNow);

        Assert.Null(store.LastKilled(new RoomKey(1, 1)));
        Assert.Null(store.LastKilled(new RoomKey(5, 100)));
    }

    [Fact]
    public void ResetArrivalsFor_DropsKillsToo()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        store.NoteKill(DateTimeOffset.UtcNow);

        store.ResetArrivalsFor(new[] { lair });

        Assert.Null(store.LastKilled(lair));
        Assert.Null(store.ClockStart(lair));
        Assert.Null(store.NextReadyAt(lair));
    }

    [Fact]
    public void ResetArrivals_NoCrash_OnEmptyStore()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        store.ResetArrivals();
        Assert.Null(store.LastEntered(new RoomKey(5, 100)));
    }
}
