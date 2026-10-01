using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

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
            InStock = true, InParadigm = true, StopBefore = false }]);

        // The seed's Stop before is on by default; the user's explicit off wins.
        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        Assert.False(Assert.Single(reloaded.Resolve()).StopBefore);
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

    // Stop before ships off for the bosses that won't attack on sight — the Neutral
    // ones, sheriff lionheart, justicar halford and mayor godfrey — and for the gigantic black ooze,
    // which is hostile but can't be avoided in the labyrinth (user, 2026-10-01).
    // Every other seed boss leaves it on.
    [Fact]
    public void ShippedSeed_StopBeforeIsOffOnlyForTheListedBosses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-boss-seed-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<BossDef> seed = JsonStore.Load<List<BossDef>>(Path.Combine(dir, "BossDefs.seed.json"))!;
            // The live flag and its reset default ship in step.
            Assert.All(seed, b => Assert.Equal(b.StopBefore, b.ResetStopBefore));
            Assert.All(seed, b => Assert.False(b.ResetGrabAll));
            string[] off = seed.Where(b => !b.StopBefore).Select(b => b.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "animated juggernaut",
                "aquilas",
                "argak the grey",
                "catoblepas",
                "choira pyromancer",
                "cocoon",
                "darem tidegrasp",
                "enigma lord",
                "fair maiden",
                "fallen champion",
                "giant pulsating cocoon",
                "giant river turtle",
                "giant roc",
                "gigantic black ooze",
                "grakh bonegrinder",
                "grand master",
                "hanging cocoon",
                "justicar halford",
                "kai master",
                "lallim whitemane",
                "lord chisholm",
                "massive cocoon",
                "mayor godfrey",
                "mayor of arlysia",
                "remik of the ebon blade",
                "sharh'kur",
                "sheriff lionheart",
                "storm giant commander",
                "storm giant king",
                "volodar",
                "wandering cleric",
                "woodelf lord",
            }, off);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp cleanup */ }
        }
    }

    // Each boss carries its own reset defaults; an edit to one is a delta the overlay keeps.
    [Fact]
    public void ResetDefaults_ArePerBoss_AndSurviveASave()
    {
        Assert.True(new BossDef().ResetStopBefore);
        Assert.False(new BossDef().ResetGrabAll);

        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        BossDef edited = Assert.Single(s.Resolve());
        edited.DefaultStopBefore = false;
        edited.DefaultGrabAll = true;
        s.Save([edited]);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        BossDef back = Assert.Single(reloaded.Resolve());
        Assert.False(back.ResetStopBefore);
        Assert.True(back.ResetGrabAll);
        Assert.True(back.StopBefore);   // the live flag is untouched by a default edit
    }

    // An overlay entry saved before the reset defaults existed doesn't state them, so
    // it resets the way the seed boss does.
    [Fact]
    public void OverlayEntryWithoutResetDefaults_TakesTheSeedBosss()
    {
        BossDef quiet = Boss("mayor godfrey", rooms: "1/1");
        quiet.StopBefore = false; quiet.DefaultStopBefore = false;
        WriteSeed(quiet);
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        s.Save([new BossDef { Name = "mayor godfrey", MonsterNumber = 1, Rooms = new() { "1/2" },
            InStock = true, InParadigm = true, StopBefore = false }]);

        BossStore reloaded = new(seedPath: _seedPath); reloaded.OnActiveSetChanged(_scratchSet);
        BossDef back = Assert.Single(reloaded.Resolve());
        Assert.Equal(new[] { "1/2" }, back.Rooms);
        Assert.False(back.ResetStopBefore);
    }

    // A boss stops the walk one room short unless the user turns that off.
    [Fact]
    public void StopBefore_DefaultsOn()
    {
        Assert.True(new BossDef().StopBefore);
        WriteSeed(Boss("cyclops", rooms: "7/730"));
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        Assert.True(Assert.Single(s.Resolve()).StopBefore);
    }

    [Fact]
    public void MatchesSeed_DetectsRoomAndFlagEdits()
    {
        BossDef seed = Boss("x", rooms: "1/1");
        Assert.True(seed.Clone().MatchesSeed(seed));
        BossDef edited = seed.Clone(); edited.StopBefore = !seed.StopBefore;
        Assert.False(edited.MatchesSeed(seed));
        BossDef reroom = seed.Clone(); reroom.Rooms = new() { "1/2" };
        Assert.False(reroom.MatchesSeed(seed));
    }

    // Timer-sync adoption relies on recovering a boss the user REMOVED from their list
    // (the case they care about) — by MDB number or name — even though Resolve() hides
    // it. A genuine non-match returns null.
    [Fact]
    public void FindInCatalog_RecoversRemovedSeedBoss_ByNumberOrName()
    {
        WriteSeed(new BossDef { Name = "shadow lord", MonsterNumber = 412, InStock = true, InParadigm = true });
        BossStore s = new(seedPath: _seedPath); s.OnActiveSetChanged(_scratchSet);
        s.Save(Array.Empty<BossDef>());          // remove it (Removed tombstone)
        Assert.Empty(s.Resolve());               // hidden from the normal list

        Assert.Equal("shadow lord", s.FindInCatalog(412, null)?.Name);
        Assert.Equal("shadow lord", s.FindInCatalog(null, "shadow lord")?.Name);
        Assert.Null(s.FindInCatalog(999, "nobody"));
    }
}
