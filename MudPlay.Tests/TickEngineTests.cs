using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class TickEngineTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, TickEngine tick) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        TickEngine tick = new(router);
        return (router, tick);
    }

    // ----- regen gains keep the round grid true ----------------------------

    private static (MessageRouter router, TickEngine tick, Func<DateTimeOffset> now, Action<double> advance) SetupClocked()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        TickEngine tick = new(router, () => now);
        return (router, tick, () => now, seconds => now += TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void ManaGain_StartsTheRoundGrid_AnHpGainDoesNot()
    {
        var (_, tick, now, _) = SetupClocked();

        tick.NoteGridTick(now(), authoritative: false);
        Assert.Null(tick.LastCombatTick);

        tick.NoteGridTick(now(), authoritative: true);
        Assert.Equal(now(), tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void HpGain_OnlyFineTunesTheGrid()
    {
        var (_, tick, now, advance) = SetupClocked();
        tick.NoteGridTick(now(), authoritative: true);
        DateTimeOffset start = now();

        advance(0.4);                                   // the grid is 0.4 s later than projected
        tick.NoteGridTick(now(), authoritative: false);
        Assert.Equal(start + TimeSpan.FromSeconds(0.4), tick.LastCombatTick);

        advance(2.0);                                   // a heal over time, off the grid
        tick.NoteGridTick(now(), authoritative: false);
        Assert.Equal(start + TimeSpan.FromSeconds(0.4), tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void ManaGain_MovesTheGridByAnyAmount_UnlessARoundWasJustSeen()
    {
        var (router, tick, now, advance) = SetupClocked();
        tick.NoteGridTick(now(), authoritative: true);
        advance(2.0);
        tick.NoteGridTick(now(), authoritative: true);
        Assert.Equal(now(), tick.LastCombatTick);

        advance(3.0);
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));   // a round seen on the wire
        DateTimeOffset seen = now();
        advance(2.0);
        tick.NoteGridTick(now(), authoritative: true);                   // two seconds off a seen round
        Assert.Equal(seen, tick.LastCombatTick);
        tick.Dispose();
    }

    // The projection running a little late for a round the gain has just marked:
    // the timer fires that round at once and lands on the gain.
    [Fact]
    public void GainAheadOfTheProjection_FiresThatRoundOnTheNextPoll()
    {
        var (_, tick, now, advance) = SetupClocked();
        tick.NoteGridTick(now(), authoritative: true);
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        advance(4.8);
        tick.NoteGridTick(now(), authoritative: false);
        Assert.Equal(0, fires);                          // nothing fires from the gain itself
        tick.PollTimersForTests();

        Assert.Equal(1, fires);
        Assert.Equal(now(), tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void UserHitsDamageLine_FiresCombatTickAndStampsTimestamp()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        Assert.Null(tick.LastCombatTick);
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));

        Assert.Equal(1, fires);
        Assert.NotNull(tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void MobHitsDamageLine_AlsoFiresCombatTick()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("The Goblin slashes you for 4 damage!"));

        Assert.Equal(1, fires);
        tick.Dispose();
    }

    [Fact]
    public void CombatTick_ReportsDamageDrivenVsTimerFallbackSource()
    {
        // A tick fired straight off a combat line is flagged damage-driven (the
        // round's prompt hasn't landed, so HP is stale); the 5s timer fallback is
        // flagged HP-fresh. CastingDirector reads this to hold its non-heal casts
        // on a stale-HP tick (report paradigm-20260904-214056).
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        using TickEngine tick = new(router, () => now);
        bool? sourceAtFire = null;
        tick.CombatTickElapsed += () => sourceAtFire = tick.LastCombatTickWasDamageDriven;

        router.Dispatch(Line("The Goblin slashes you for 4 damage!"));
        Assert.True(sourceAtFire);                       // damage-line-driven
        Assert.True(tick.LastCombatTickWasDamageDriven);

        sourceAtFire = null;
        now += TickEngine.CombatTickInterval;
        tick.PollTimersForTests();                        // projected round, no new line
        Assert.False(sourceAtFire);                       // timer fallback → HP-fresh
        Assert.False(tick.LastCombatTickWasDamageDriven);
    }

    [Fact]
    public void NonDamageLines_DontFireCombatTick()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("Forged gossips: hi"));
        router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Equal(0, fires);
        Assert.Null(tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void EnsureCombatTickAnchor_SeedsFreshFallbackAndFiresProjectedRound()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        using TickEngine tick = new(router, () => now);
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        Assert.Null(tick.LastCombatTick);
        tick.EnsureCombatTickAnchor();

        Assert.Equal(now, tick.LastCombatTick);
        Assert.Equal(0, fires); // the projected round, not the current one, retries

        now += TickEngine.CombatTickInterval;
        tick.PollTimersForTests();

        Assert.Equal(1, fires);
        Assert.Equal(now, tick.LastCombatTick);
    }

    [Fact]
    public void EnsureCombatTickAnchor_DoesNotMoveObservedCombatCadence()
    {
        var (router, tick) = Setup();
        router.Dispatch(Line("The Goblin slashes you for 4 damage!"));
        DateTimeOffset observed = Assert.IsType<DateTimeOffset>(tick.LastCombatTick);

        tick.EnsureCombatTickAnchor();

        Assert.Equal(observed, tick.LastCombatTick);
        tick.Dispose();
    }

    [Fact]
    public void CombatTickInterval_IsFiveSeconds()
    {
        // Pinning the universal MajorMUD combat-tick value so a stray
        // refactor doesn't silently change it. The spec is explicit:
        // 5 s is invariant across realm flavours.
        Assert.Equal(TimeSpan.FromSeconds(5), TickEngine.CombatTickInterval);
    }

    [Fact]
    public void RegenIntervals_DefaultToZero_DisablingRegenEvents()
    {
        var (_, tick) = Setup();
        Assert.Equal(TimeSpan.Zero, tick.HpRegenInterval);
        Assert.Equal(TimeSpan.Zero, tick.ManaRegenInterval);
        Assert.Null(tick.TimeToNextHpRegenTick);
        Assert.Null(tick.TimeToNextManaRegenTick);
        tick.Dispose();
    }

    [Fact]
    public void Dispose_StopsCombatTickEvents()
    {
        var (router, tick) = Setup();
        int fires = 0;
        tick.CombatTickElapsed += () => fires++;

        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        Assert.Equal(1, fires);

        tick.Dispose();
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        Assert.Equal(1, fires);
    }
}
