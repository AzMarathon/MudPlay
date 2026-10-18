using MudPlay.Game;
using MudPlay.Game.Health;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// DeathFloorTracer slow-vs-overkill classification and the
// death-floor refine writeback. A slow death (gradual bleed to the floor)
// refines RealmProfile.PlayerDiesAtHp; an overkill (killed from
// positive HP, or a big single drop past the floor) is discarded.
public sealed class DeathFloorTracerTests
{
    private sealed class Harness : IDisposable
    {
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RealmProfile? Realm { get; set; } = new() { Name = "TestRealm", PlayerDiesAtHp = -25 };
        public int SaveCount { get; private set; }
        public DeathFloorTracer Tracer { get; }

        public Harness()
        {
            Tracer = new DeathFloorTracer(
                State,
                resolveRealm: () => Realm is { } r ? (new BbsProfile { Name = "TestBBS", Realms = { r } }, r) : null,
                saveBbs: _ => SaveCount++,
                log: Log);
        }

        // Latest observed max HP the classifier's big-step guard divides by.
        public void SetMaxHp(int maxHp) => State.MaxHp = maxHp;

        // Feed a sequence of HP readings in order (each fires PlayerState.Hp's
        // PropertyChanged exactly as PromptParser would). The default Hp starts
        // at 0, so begin descents from a positive value to establish a prior.
        public void Feed(params int[] hps)
        {
            foreach (int hp in hps) State.Hp = hp;
        }

        public void Dispose() => Tracer.Dispose();
    }

    // ----- slow deaths lower the floor --------------------------------
    //
    // The HP a death comes at is never printed: the last prompt before the death
    // line is an HP the character survived, so the floor is at least one below it.

    // A slow death whose last reading is above the floor says nothing new: the
    // floor is never raised to it.
    [Fact]
    public void SlowDeath_ShallowerThanTheFloor_NeverRaisesIt()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Alive, then dropped and bleeding gradually (steps of 4) to -20.
        h.Feed(40, 0, -4, -8, -12, -16, -20);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    // Bleeding -5, -6, -7, then a hit kills. -7 was the last HP survived; saving it
    // as the floor would read the character as dead from -8 down to the real floor,
    // where the emergency hang-up stops firing.
    [Fact]
    public void BleedThenAHitKills_TheFloorStaysWhereItWas()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(40, 0, -5, -6, -7);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void SlowDeath_DeeperThanSeed_LowersToOneBelowTheLastHpSurvived()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(30, 0, -8, -16, -24, -32, -40);      // bleed steps of 8, last read -40
        h.Tracer.RecordDeath();

        Assert.Equal(-41, h.Realm!.PlayerDiesAtHp);
        // Two writes: the live-survival path lowers it to -33 the moment the char is
        // seen bleeding on from a survived -32 (below the -25 seed), then the death
        // shows -40 was survived too.
        Assert.Equal(2, h.SaveCount);
    }

    // A last reading at the floor itself proves the floor is deeper.
    [Fact]
    public void SlowDeath_LastReadAtTheFloor_LowersItByOne()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(40, 0, -5, -10, -15, -20, -25);
        h.Tracer.RecordDeath();

        Assert.Equal(-26, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(1, h.SaveCount);
    }

    // ----- overkills are discarded -----------------------------------

    [Fact]
    public void Overkill_KilledFromPositiveHp_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Never entered the bleeding-out band — last reading is positive.
        h.Feed(150, 80);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void Overkill_SinglePlungeIntoBand_NoGradualBleed_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // One blow from +80 straight to -60 — no in-band bleed tick observed.
        h.Feed(150, 80, -60);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void Overkill_BigHitWhileDropped_BlowsPastFloor_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Bleeding gently (steps of 3), then a 64-HP blow blows past the floor.
        // 64 > 10% of 200 max HP → flagged a combat hit, discarded.
        h.Feed(40, 0, -3, -6, -70);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    // ----- live-survival refine (no death) ---------------------------

    [Fact]
    public void LiveSurvival_BelowSeed_RefinesFloorWithoutDeath()
    {
        using Harness h = new();
        h.SetMaxHp(64);
        // Report 193417: one hard hit from +1 to -49 (survived, bleeding), then
        // the reading moves to -48 — a second in-band prompt that proves the
        // character was alive at -49. Floor refines to -50 (one below the deepest
        // survived reading); no death is recorded.
        h.Feed(1, -49, -48);

        Assert.Equal(-50, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(1, h.SaveCount);
    }

    [Fact]
    public void LiveSurvival_TerminalDeathReading_NeverRefinesFloor()
    {
        using Harness h = new();
        h.SetMaxHp(64);
        // Survives -49, then a killing overkill hit drives HP to -251. The -251
        // is the terminal reading (no later in-band prompt), so it can never
        // refine the floor; only the survived -49 does (→ -50). The overkill death
        // is then discarded by the slow-death classifier.
        h.Feed(1, -49, -251);
        h.Tracer.RecordDeath();

        Assert.Equal(-50, h.Realm!.PlayerDiesAtHp);   // from surviving -49, not the -251 death
        Assert.Equal(1, h.SaveCount);
    }

    [Fact]
    public void LiveSurvival_ShallowerThanSeed_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(64);
        // Surviving shallow negatives (well above the -25 seed) tells us nothing
        // new — the floor is already believed to be deeper.
        h.Feed(1, -5, -10);

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void LiveSurvival_AutoRefineOff_DoesNotRefine()
    {
        using Harness h = new();
        h.Realm!.AutoRefineDeathFloor = false;
        h.SetMaxHp(64);
        h.Feed(1, -49, -48);

        Assert.Equal(-25, h.Realm.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    // ----- guards & lifecycle ----------------------------------------

    [Fact]
    public void MaxHpUnknown_CannotBoundBleedSteps_DoesNotRefine()
    {
        using Harness h = new();
        // MaxHp left at 0 (never observed) — the big-step guard can't run.
        h.Feed(40, 0, -5, -10, -15, -20);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void AutoRefineOff_PinsManualValue()
    {
        using Harness h = new();
        h.Realm!.AutoRefineDeathFloor = false;
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20);       // a clean slow death
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void NoActiveBbs_DoesNotThrowOrSave()
    {
        using Harness h = new();
        h.Realm = null;
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20);
        h.Tracer.RecordDeath();                      // must not throw

        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void HealingMidBleed_ResetsDescent_ThenPositiveDeathDiscarded()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Bled into the band, then a heal lifts HP positive (descent resets),
        // then killed from positive HP — no measurable bleed remains.
        h.Feed(40, 0, -5, -10, 50, 20);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Realm!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void SecondDeath_WithoutNewReadings_DoesNotRefineAgain()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20, -24, -28);
        h.Tracer.RecordDeath();                      // refines once
        Assert.Equal(1, h.SaveCount);

        // Descent was reset by the first death; a second RecordDeath with no
        // fresh trajectory has nothing to measure.
        h.Tracer.RecordDeath();
        Assert.Equal(1, h.SaveCount);
        Assert.Equal(-29, h.Realm!.PlayerDiesAtHp);
    }
}
