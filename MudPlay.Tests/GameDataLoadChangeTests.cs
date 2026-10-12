using System;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// What became of the game data between two loads of it, told from the cache's own
// loads: nothing, another set, or the same set read in again. (The stop made for
// each is in GameDataSwitchStopsMovementTests, wired as the app wires it.)
public sealed class GameDataLoadChangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-change-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private GameDataCache CacheWith(params string[] sets)
    {
        foreach (string set in sets)
        {
            Directory.CreateDirectory(Path.Combine(_root, set));
            File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), "[]");
        }
        return new GameDataCache(_root);
    }

    [Fact]
    public void TheSameLoad_IsNoChange()
    {
        GameDataCache cache = CacheWith("alpha");
        cache.SwitchSet("alpha");
        GameDataCache.Load load = cache.ActiveLoad;

        GameDataLoadChange change = GameDataLoadChange.Between(load, cache.ActiveLoad);

        Assert.Equal(GameDataLoadChange.Kind.Nothing, change.What);
        Assert.False(change.Changed);
        Assert.Equal(default, change);
    }

    // The cache answers a SwitchSet to the active name, in any capitals, without a
    // new load and without raising its event.
    [Fact]
    public void ASwitchSetToTheActiveName_IsNoChange()
    {
        GameDataCache cache = CacheWith("alpha");
        cache.SwitchSet("alpha");
        GameDataCache.Load was = cache.ActiveLoad;
        int raised = 0;
        cache.ActiveSetChanged += _ => raised++;

        cache.SwitchSet("alpha");
        cache.SwitchSet("ALPHA");

        Assert.False(GameDataLoadChange.Between(was, cache.ActiveLoad).Changed);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheFirstSet_IsAnotherSet_FromNone()
    {
        GameDataCache cache = CacheWith("alpha");
        GameDataCache.Load was = cache.ActiveLoad;

        cache.SwitchSet("alpha");
        GameDataLoadChange change = GameDataLoadChange.Between(was, cache.ActiveLoad);

        Assert.Equal(new GameDataLoadChange(GameDataLoadChange.Kind.AnotherSet, null, "alpha"), change);
        Assert.Equal("the game data changed from '(none)' to 'alpha'", change.Why);
    }

    [Fact]
    public void ASwitch_IsAnotherSet_WithBothNames()
    {
        GameDataCache cache = CacheWith("alpha", "beta");
        cache.SwitchSet("alpha");
        GameDataCache.Load was = cache.ActiveLoad;

        cache.SwitchSet("beta");
        GameDataLoadChange change = GameDataLoadChange.Between(was, cache.ActiveLoad);

        Assert.Equal(new GameDataLoadChange(GameDataLoadChange.Kind.AnotherSet, "alpha", "beta"), change);
        Assert.False(change.ReadInAgain);
        Assert.Equal("the game data changed from 'alpha' to 'beta'", change.Why);
    }

    // An import over the active set and the Game Data menu's pick of the set already
    // ticked both reach the cache as ReloadActiveSet.
    [Fact]
    public void TheActiveSetReloaded_IsTheSameSetReadInAgain()
    {
        GameDataCache cache = CacheWith("alpha");
        cache.SwitchSet("alpha");
        GameDataCache.Load was = cache.ActiveLoad;

        cache.ReloadActiveSet();
        GameDataLoadChange change = GameDataLoadChange.Between(was, cache.ActiveLoad);

        Assert.Equal(new GameDataLoadChange(GameDataLoadChange.Kind.ReadInAgain, "alpha", "alpha"), change);
        Assert.True(change.ReadInAgain);
        Assert.Equal("game data 'alpha' was read in again", change.Why);
    }

    // Two loads of one name with another set in between are not one load: whatever
    // was worked out on the first was worked out on rooms since dropped.
    [Fact]
    public void AwayToAnotherSetAndBack_IsAChange()
    {
        GameDataCache cache = CacheWith("alpha", "beta");
        cache.SwitchSet("alpha");
        GameDataCache.Load was = cache.ActiveLoad;

        cache.SwitchSet("beta");
        cache.SwitchSet("alpha");

        Assert.True(GameDataLoadChange.Between(was, cache.ActiveLoad).Changed);
    }

    [Fact]
    public void TheSetUnloaded_IsAnotherSet_ToNone()
    {
        GameDataCache cache = CacheWith("alpha");
        cache.SwitchSet("alpha");
        GameDataCache.Load was = cache.ActiveLoad;

        cache.SwitchSet(null);
        GameDataLoadChange change = GameDataLoadChange.Between(was, cache.ActiveLoad);

        Assert.Equal(new GameDataLoadChange(GameDataLoadChange.Kind.AnotherSet, "alpha", null), change);
        Assert.Equal("the game data changed from 'alpha' to '(none)'", change.Why);
    }
}
