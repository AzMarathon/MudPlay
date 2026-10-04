using System;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The Exp/Hr simulator's regen for the character the Paradigm timing was measured
// on (report paradigm-20261004-024314: level 17, Health 60, no regen gear). The wire
// showed +1 standing every 10 s and, resting, +1 +1 +1 +4 +4 +4 every 5 s.
public sealed class SimRegenBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-simregen-" + Path.GetRandomFileName());
    private readonly GameDataCache _cache;

    public SimRegenBuildTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "set"));
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("set");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private SimRegen Build(RealmType realm)
    {
        PlayerStats stats = new() { Level = 17, Health = 60 };
        return SimCharacterBuilder.BuildRegen(stats, Array.Empty<EquippedItem>(), _cache, questBonuses: null, realm);
    }

    [Fact]
    public void Paradigm_MatchesTheCapture()
    {
        SimRegen regen = Build(RealmType.ParaMud);

        Assert.Equal(1, regen.HpStanding(0));
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 4.0, 4.0, 4.0, 1.0 },
            Enumerable.Range(1, 7).Select(t => regen.RestTickHp(t)));
        Assert.True(regen.RestReplacesStanding);
        Assert.Equal(RealmRegenProfile.ParaMud, regen.Cadence);
    }

    [Fact]
    public void Stock_PaysTheIdleAmountAndAFullRestTickEveryTime()
    {
        SimRegen regen = Build(RealmType.Stock);

        Assert.Equal(2, regen.HpStanding(0));                      // (17 + 20) × 60 / 750
        Assert.Equal(new[] { 6.0, 6.0, 6.0, 6.0 }, Enumerable.Range(1, 4).Select(t => regen.RestTickHp(t)));
        Assert.False(regen.RestReplacesStanding);
    }
}
