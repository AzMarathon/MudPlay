using System;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins MonsterSpawnIndex's build + snapshot lifecycle: token classification
// (placed / assigned / lair) matches the Summoned By convention, dedup is
// correct even at a scale that would expose a reintroduced O(n) List.Contains
// scan, Warm() produces a queryable result without crashing, and a set switch
// invalidates the prior snapshot instead of leaking stale data through — the
// self-comparison replacing the old event-driven Invalidate().
public sealed class MonsterSpawnIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterSpawnIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spawnindex-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private GameDataCache NewCache(string setName, string monstersJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, setName));
        File.WriteAllText(Path.Combine(_root, setName, "Monsters.json"), monstersJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet(setName);
        return cache;
    }

    [Fact]
    public void ClassifiesPlacedAssignedAndLairTokens()
    {
        const string json = """
            [
              { "Number": 1, "Name": "orc", "Summoned By": "Room 1/1" },
              { "Number": 2, "Name": "wolf", "Summoned By": "Group(lair): 1/1" },
              { "Number": 3, "Name": "bandit", "Summoned By": "Group: 2/2" }
            ]
            """;
        GameDataCache cache = NewCache("alpha", json);
        MonsterSpawnIndex spawns = new(cache);
        RoomKey room1 = new(1, 1);
        RoomKey room2 = new(2, 2);

        Assert.Equal(new[] { 1 }, spawns.PlacedMonsterIdsAt(room1));
        Assert.Equal(new[] { 3 }, spawns.AssignedMonsterIdsAt(room2));
        // The permissive set carries every token kind, including the lair one.
        Assert.Equal(new[] { 1, 2 }, spawns.MonsterIdsSummonedAt(room1));
        Assert.Empty(spawns.PlacedMonsterIdsAt(room2));
        Assert.Empty(spawns.AssignedMonsterIdsAt(room1));
    }

    [Fact]
    public void DedupsRepeatedTokensForTheSameRoom()
    {
        // The same monster listed with the room twice (a malformed but
        // real-world-plausible Summoned By) must not double up in the result.
        const string json = """
            [
              { "Number": 1, "Name": "orc", "Summoned By": "Room 1/1, Room 1/1" }
            ]
            """;
        GameDataCache cache = NewCache("alpha", json);
        MonsterSpawnIndex spawns = new(cache);

        Assert.Equal(new[] { 1 }, spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));
    }

    // Regression for paradigm-20260911-231808: a single room referenced by many
    // distinct monster records used to dedup via a List<int>.Contains scan on
    // every insert, turning a hot room into an O(n²) build (a real ~29 s
    // UI-thread stall on Paradigm's ~215k-token Monsters.json). This pins
    // correctness at a scale that would make a reintroduced O(n) scan glaring in
    // test runtime, without asserting wall-clock time (too environment-sensitive
    // to assert directly).
    [Fact]
    public void HandlesManyDistinctMonstersInOneRoom()
    {
        const int monsterCount = 20_000;
        StringBuilder sb = new("[");
        for (int i = 1; i <= monsterCount; i++)
        {
            if (i > 1) sb.Append(',');
            sb.Append($$"""{ "Number": {{i}}, "Name": "m{{i}}", "Summoned By": "Group: 5/500" }""");
        }
        sb.Append(']');

        GameDataCache cache = NewCache("alpha", sb.ToString());
        MonsterSpawnIndex spawns = new(cache);

        var assigned = spawns.AssignedMonsterIdsAt(new RoomKey(5, 500));
        Assert.Equal(monsterCount, assigned.Count);
        Assert.Equal(monsterCount, assigned.Distinct().Count());
    }

    [Fact]
    public void NoMonstersTable_IsEmptyNotAnError()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        MonsterSpawnIndex spawns = new(cache);

        Assert.Empty(spawns.MonsterIdsSummonedAt(new RoomKey(1, 1)));
    }

    [Fact]
    public void Warm_ProducesAQueryableSnapshot()
    {
        const string json = """
            [ { "Number": 1, "Name": "orc", "Summoned By": "Room 1/1" } ]
            """;
        GameDataCache cache = NewCache("alpha", json);
        MonsterSpawnIndex spawns = new(cache);

        spawns.Warm();

        Assert.Equal(new[] { 1 }, spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));
    }

    [Fact]
    public void SwitchingActiveSet_InvalidatesThePriorSnapshot()
    {
        const string alphaJson = """
            [ { "Number": 1, "Name": "orc", "Summoned By": "Room 1/1" } ]
            """;
        const string betaJson = """
            [ { "Number": 2, "Name": "troll", "Summoned By": "Room 1/1" } ]
            """;
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), alphaJson);
        File.WriteAllText(Path.Combine(_root, "beta", "Monsters.json"), betaJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        MonsterSpawnIndex spawns = new(cache);

        Assert.Equal(new[] { 1 }, spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));

        cache.SwitchSet("beta");

        Assert.Equal(new[] { 2 }, spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));
    }
}
