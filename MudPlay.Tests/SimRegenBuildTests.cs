using System;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
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

    // A second character, with HP-regen gear (report paradigm-20261004-140923: level
    // 28, Health 71, +25% from a ring). The wire showed +2 +2 +3 standing, the +3 with
    // the mana tick, and resting +2 +2 +3 +7 +7 +7: thirds of the amount before the
    // bonus, the bonus's one point on the last of each three, and 6 + 1 as the full gain.
    [Fact]
    public void Paradigm_RegenBonus_ComesOnTheLastOfEachThree()
    {
        PlayerStats stats = new() { Level = 28, Health = 71 };
        SimRegen regen = SimCharacterBuilder.BuildRegen(
            stats, Array.Empty<EquippedItem>(), _cache, questBonuses: null, RealmType.ParaMud);

        Assert.Equal(2, regen.HpStanding(25));
        Assert.Equal(1, regen.StandingPassExtra(25));
        Assert.Equal(new[] { 2.0, 2.0, 3.0, 7.0, 7.0, 7.0, 2.0, 2.0, 3.0, 7.0 },
            Enumerable.Range(1, 10).Select(t => regen.RestTickHp(t, 25)));

        // The same character with the ring off (report paradigm-20261004-142359):
        // +2 +2 +2 +6 +6 +6.
        Assert.Equal(0, regen.StandingPassExtra(0));
        Assert.Equal(new[] { 2.0, 2.0, 2.0, 6.0, 6.0, 6.0, 2.0 }, Enumerable.Range(1, 7).Select(t => regen.RestTickHp(t)));
    }

    [Fact]
    public void Stock_PaysTheIdleAmountAndAFullRestTickEveryTime()
    {
        SimRegen regen = Build(RealmType.Stock);

        Assert.Equal(2, regen.HpStanding(0));                      // (17 + 20) × 60 / 750
        Assert.Equal(new[] { 6.0, 6.0, 6.0, 6.0 }, Enumerable.Range(1, 4).Select(t => regen.RestTickHp(t)));
        Assert.False(regen.RestReplacesStanding);
    }

    // Paradigm's standing gain and low rest gain: a third of the 30 s amount before
    // any regen bonus, the fraction dropped, never under 1.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(6, 2)]
    [InlineData(13, 4)]
    public void ParadigmHpRegenThird_DropsTheFraction_NeverUnderOne(int idle, int third) =>
        Assert.Equal(third, CharacterCalculator.ParadigmHpRegenThird(idle));
}
