using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// BossStore resolution + per-set delta overlay, mirroring QuestStoreTests: the
// overlay wins over the universal seed, a Removed tombstone hides a seed boss,
// added bosses surface, realm flags gate visibility, and Save keeps the overlay a
// delta. Uses a unique scratch set under AppPaths.DataRoot + a temp seed file.
public sealed class BossStoreTests : IDisposable
{
    private readonly string _scratchSet = "boss-test-" + Path.GetRandomFileName();
    private readonly string _seedPath = Path.Combine(Path.GetTempPath(), "bossseed-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { string f = AppPaths.GameDataSetDir(_scratchSet); if (Directory.Exists(f)) Directory.Delete(f, true); } catch { }
        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); } catch { }
    }

    private void WriteSeed(params BossDef[] defs) => JsonStore.Save(_seedPath, defs.ToList());

    private static BossDef Boss(string name, bool stock = true, bool para = true, params string[] rooms) => new()
    {
        Name = name, MonsterNumber = 1, Rooms = rooms.ToList(),
        InStock = stock, InParadigm = para, RespawnType = BossRespawnType.Timed,
    };

    [Fact]
    public void Resolve_SeedOnly_ReturnsSeed()
    {
        WriteSeed(Boss("crimson mist", rooms: "3/560"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);

        BossDef b = Assert.Single(s.Resolve());
        Assert.Equal("crimson mist", b.Name);
        Assert.Equal(new[] { "3/560" }, b.Rooms);
    }

    [Fact]
    public void Resolve_OverlayWins_OverSeed()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        s.Save([new BossDef { Name = "cyclops", MonsterNumber = 1, Rooms = new() { "7/730" },
            InStock = true, InParadigm = true, StopBefore = true }]);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        Assert.True(Assert.Single(reloaded.Resolve()).StopBefore);
    }

    [Fact]
    public void Resolve_RemovedTombstone_HidesSeedBoss()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"), Boss("chimera", rooms: "3/583"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);

        s.Save([s.Resolve().First(b => b.Name == "chimera")]);   // cyclops omitted → removed

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal(new[] { "chimera" }, reloaded.Resolve().Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Resolve_UserAddedBoss_Surfaces()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);

        List<BossDef> list = s.Resolve().ToList();
        list.Add(Boss("my custom boss", rooms: "1/100"));
        s.Save(list);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Contains(reloaded.Resolve(), b => b.Name == "my custom boss");
    }

    [Fact]
    public void ResolveForRealm_Stock_HidesParadigmOnly()
    {
        WriteSeed(Boss("shared boss", stock: true, para: true, rooms: "1/1"),
                  Boss("para only boss", stock: false, para: true, rooms: "5/5"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);

        Assert.Equal(2, s.ResolveForRealm(RealmType.ParaMud).Count);
        Assert.Equal(new[] { "shared boss" }, s.ResolveForRealm(RealmType.Stock).Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Save_UnchangedBoss_IsDropped_SoLaterSeedFlowsThrough()
    {
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        s.Save(s.Resolve());   // re-save unchanged

        // A changed seed (same set) now resolves through — proving no frozen overlay.
        WriteSeed(new BossDef { Name = "cyclops", MonsterNumber = 1, Rooms = new() { "7/999" }, InStock = true, InParadigm = true });
        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal(new[] { "7/999" }, Assert.Single(reloaded.Resolve()).Rooms);
    }

    [Fact]
    public void Save_NoActiveSet_IsNoOp()
    {
        BossStore s = new(seedPath: _seedPath);   // never OnActiveSetChanged
        s.Save([Boss("x", rooms: "1/1")]);          // must not throw
        Assert.Null(s.ActiveSet);
    }

    [Fact]
    public void MatchesSeed_DetectsRoomAndFlagEdits()
    {
        BossDef seed = Boss("x", rooms: "1/1");
        Assert.True(seed.Clone().MatchesSeed(seed));
        BossDef edited = seed.Clone(); edited.StopBefore = true;
        Assert.False(edited.MatchesSeed(seed));
        BossDef reroom = seed.Clone(); reroom.Rooms = new() { "1/2" };
        Assert.False(reroom.MatchesSeed(seed));
    }
}
