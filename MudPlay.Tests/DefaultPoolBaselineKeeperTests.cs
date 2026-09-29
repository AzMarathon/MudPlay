using MudPlay.Game.Health;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The Default-gear pool baseline: recorded only from a `stat` with the Default set on,
// stale when the level or the Default set's pool bonus changes, refreshed by a `stat`
// sent once the Default set is back on.
public sealed class DefaultPoolBaselineKeeperTests
{
    private sealed class Harness
    {
        public DefaultPoolBaseline? Stored;
        public int Level = 44;
        public (int Hp, int Ma)? Gear = (25, 0);
        public bool DefaultWorn = true;
        public bool CanCheck = true;
        public int StatsSent;
        public DateTimeOffset Now = new(2026, 9, 28, 22, 0, 0, TimeSpan.Zero);
        public DefaultPoolBaselineKeeper Keeper { get; }

        public Harness() => Keeper = new DefaultPoolBaselineKeeper(
            () => Stored, b => Stored = b, () => Level, () => Gear, () => DefaultWorn,
            () => CanCheck, () => StatsSent++, () => Now);
    }

    [Fact]
    public void StatWithDefaultOn_Records()
    {
        Harness h = new();
        h.Keeper.OnStatScreen(503, 398);

        Assert.Equal(503, h.Stored!.MaxHp);
        Assert.Equal(398, h.Stored.MaxMa);
        Assert.False(h.Keeper.IsStale);
    }

    // The report's case: a `stat` with the Pre-rest Mana set on mustn't move the basis.
    [Fact]
    public void StatWithOtherGearOn_Ignored()
    {
        Harness h = new();
        h.Keeper.OnStatScreen(503, 398);
        h.DefaultWorn = false;
        h.Keeper.OnStatScreen(478, 423);

        Assert.Equal(398, h.Stored!.MaxMa);
    }

    [Fact]
    public void LevelUpOrDefaultGearChange_Stale_AsksForStatOnceDefaultIsOn()
    {
        Harness h = new();
        h.Keeper.OnStatScreen(503, 398);

        h.Level = 45;
        Assert.True(h.Keeper.IsStale);
        h.DefaultWorn = false;
        h.Keeper.Poll();
        Assert.Equal(0, h.StatsSent);            // not while other gear is on

        h.DefaultWorn = true;
        h.Keeper.Poll();
        h.Keeper.Poll();
        Assert.Equal(1, h.StatsSent);            // once, then throttled
        Assert.Equal(398, h.Stored!.MaxMa);      // the old baseline stays until it lands

        h.Keeper.OnStatScreen(520, 410);
        Assert.False(h.Keeper.IsStale);

        h.Gear = (25, 10);                        // Default set's mana bonus changed
        Assert.True(h.Keeper.IsStale);
    }
}
