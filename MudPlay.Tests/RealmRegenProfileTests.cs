using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

public sealed class RealmRegenProfileTests
{
    [Fact]
    public void Stock_MatchesTheEngineCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RealmRegenProfile.Stock.StandingInterval);
        Assert.Equal(TimeSpan.FromSeconds(21), RealmRegenProfile.Stock.RestingInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), RealmRegenProfile.Stock.MeditatingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), RealmRegenProfile.Stock.ManaInterval);
        Assert.False(RealmRegenProfile.Stock.RestingOnRoundGrid);   // counted from the command
    }

    [Fact]
    public void ParaMud_UsesTheMeasuredCadence()
    {
        // Timed captures (report paradigm-20261004-024314): standing HP every 10 s,
        // mana every 30 s, a rest gain every 5 s on the round grid. Meditate, timed
        // over 143 stretches of a level 49-51 Priest: every 15 s, on the mana grid.
        Assert.Equal(TimeSpan.FromSeconds(10), RealmRegenProfile.ParaMud.StandingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), RealmRegenProfile.ParaMud.ManaInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), RealmRegenProfile.ParaMud.RestingInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), RealmRegenProfile.ParaMud.MeditatingInterval);
        Assert.True(RealmRegenProfile.ParaMud.RestingOnRoundGrid);
        Assert.True(RealmRegenProfile.ParaMud.MeditatingOnManaGrid);
        Assert.False(RealmRegenProfile.Stock.MeditatingOnManaGrid);
    }

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void For_SelectsTheMatchingProfile(RealmType realm)
    {
        RealmRegenProfile expected = realm == RealmType.ParaMud
            ? RealmRegenProfile.ParaMud
            : RealmRegenProfile.Stock;
        Assert.Equal(expected, RealmRegenProfile.For(realm));
    }
}
