using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

public sealed class ManaRegenRerollerTests
{
    // Drives the real AbilBreakdownParser so the reroller sees the same
    // BreakdownParsed events it will in production. Config / affordability are
    // mutable so a test can flip them mid-cycle.
    private sealed class Harness
    {
        public readonly AbilBreakdownParser Parser = new();
        public ManaRegenRerollConfig Config = new(Threshold: 5, Cap: 3);
        public bool CanAfford = true;
        public bool UseTickMonitor;   // false = Paradigm (abil 145); true = Stock (tick)
        public int AbilQueries;
        public readonly List<string> Recasts = new();
        public readonly ManaRegenReroller Reroller;

        public Harness()
        {
            Reroller = new ManaRegenReroller(
                Parser,
                () => Config,
                () => AbilQueries++,
                Recasts.Add,
                () => CanAfford,
                () => UseTickMonitor);
        }

        // Replay one abil-145 block whose spells: slice rolled `roll`, then the
        // prompt that flushes it — the parser fires BreakdownParsed on flush.
        public void FeedRoll(int roll)
        {
            Parser.FeedTestLine($"spells:   ManaRegen(145)   {roll}");
            Parser.FeedTestLine("", isPromptLine: true);
        }
    }

    [Fact]
    public void LandingWhileIdleOpensCycleAndQueriesAbil()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");

        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(0, h.Reroller.RerollsUsed);
        Assert.Equal(1, h.AbilQueries);
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void RollAtOrAboveThresholdAcceptsWithoutRecast()
    {
        Harness h = new();               // threshold 5

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(7);

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void RollBelowThresholdRecastsOnce()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(2);                   // 2 < 5

        Assert.Equal(new[] { "ntap" }, h.Recasts);
        Assert.Equal(1, h.Reroller.RerollsUsed);
        Assert.True(h.Reroller.CycleActive);   // still waiting for the recast to land
    }

    [Fact]
    public void RerollCounterSurvivesTheContinuationLanding()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(2);                   // reroll #1, recast fired
        h.Reroller.OnRollSpellLanded("ntap");   // the recast landed (continuation)

        Assert.Equal(1, h.Reroller.RerollsUsed);   // not reset by the continuation
        Assert.Equal(2, h.AbilQueries);            // queried again
    }

    [Fact]
    public void RerollsUpToCapThenAcceptsWhateverLanded()
    {
        Harness h = new();               // threshold 5, cap 3

        // Initial landing, then keep feeding bad rolls and replaying each
        // recast's landing until the cycle closes.
        h.Reroller.OnRollSpellLanded("ntap");
        int guard = 0;
        while (h.Reroller.CycleActive && guard++ < 10)
        {
            h.FeedRoll(1);               // always below threshold
            if (h.Reroller.CycleActive)
                h.Reroller.OnRollSpellLanded("ntap");   // that recast landed
        }

        Assert.Equal(3, h.Recasts.Count);          // exactly cap rerolls
        Assert.False(h.Reroller.CycleActive);       // accepted on the cap
    }

    [Fact]
    public void PausesAtManaFloorInsteadOfGivingUp()
    {
        Harness h = new() { CanAfford = false };   // threshold 5, cap 3

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                   // below threshold but can't pay to reroll now

        Assert.Empty(h.Recasts);                   // didn't recast — out of mana
        Assert.True(h.Reroller.CycleActive);       // cycle SUSPENDED, not ended
        Assert.True(h.Reroller.WaitingForMana);
        Assert.Equal(0, h.Reroller.RerollsUsed);   // the floored attempt wasn't spent
    }

    [Fact]
    public void ResumesRerollingOnceManaRecovers()
    {
        Harness h = new() { CanAfford = false };

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                   // paused at the floor
        Assert.True(h.Reroller.WaitingForMana);

        h.Reroller.OnRecoveryTick();     // still can't afford — stays paused
        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.WaitingForMana);

        h.CanAfford = true;              // meditation refilled the pool
        h.Reroller.OnRecoveryTick();     // now it resumes the next reroll

        Assert.Equal(new[] { "ntap" }, h.Recasts);
        Assert.Equal(1, h.Reroller.RerollsUsed);
        Assert.False(h.Reroller.WaitingForMana);
        Assert.True(h.Reroller.CycleActive);
    }

    [Fact]
    public void RecoveryTickNoOpsWhenNotWaitingForMana()
    {
        Harness h = new();

        h.Reroller.OnRecoveryTick();                 // idle cycle
        Assert.Empty(h.Recasts);

        h.Reroller.OnRollSpellLanded("ntap");        // awaiting abil, not mana
        h.Reroller.OnRecoveryTick();
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void NullThresholdDisablesRerollingEntirely()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: null, Cap: 3) };

        h.Reroller.OnRollSpellLanded("ntap");

        Assert.Equal(0, h.AbilQueries);            // no abil read at all
        Assert.False(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void ThresholdClearedMidCycleAcceptsTheStandingRoll()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");      // opened while threshold=5
        h.Config = h.Config with { Threshold = null };
        h.FeedRoll(1);                             // would reroll, but now disabled

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void UnrelatedAbilCodeIsIgnoredWhileAwaiting()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Parser.FeedTestLine("worn:     HPRegen(123)                0040");
        h.Parser.FeedTestLine("", isPromptLine: true);   // flushes a code-123 breakdown

        // The reroller ignored the non-145 read and is still awaiting ours.
        Assert.True(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);

        h.FeedRoll(2);                             // the real 145 read arrives
        Assert.Equal(new[] { "ntap" }, h.Recasts);
    }

    [Fact]
    public void BreakdownWithNoActiveCycleIsIgnored()
    {
        Harness h = new();

        h.FeedRoll(1);                             // no landing preceded it

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void NegativeRollBelowThresholdStillRerolls()
    {
        // A bad nature-tap / mana-flux roll subtracts from the regen rate.
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 3) };

        h.Reroller.OnRollSpellLanded("flux");
        h.FeedRoll(-50);                           // -50 < 0

        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void FreshCycleAfterAcceptZeroesTheRerollCounter()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                             // reroll #1
        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(9);                             // accept → cycle closes
        Assert.False(h.Reroller.CycleActive);

        h.Reroller.OnRollSpellLanded("ntap");      // brand-new cycle
        Assert.Equal(0, h.Reroller.RerollsUsed);
    }

    [Fact]
    public void ResetAbandonsAnInProgressCycle()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.Reset();

        Assert.False(h.Reroller.CycleActive);
        h.FeedRoll(1);                             // read arrives after reset — ignored
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void DisposeUnsubscribesFromTheParser()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.Dispose();
        h.FeedRoll(1);                             // parser fires, but reroller is detached

        Assert.Empty(h.Recasts);
        Assert.Equal(1, h.AbilQueries);            // only the pre-dispose query
    }

    // ----- IsRollSpell classifier -----------------------------------
    // The single source of truth for "this pick reroll-eligible?": a code-145
    // ability whose stored AbilVal is 0. Shared by the landing classifier and
    // the Spells tab range readout, so pin the exact signature.

    private static SpellFormulaInput FormulaWith(params SpellAbility[] abilities)
        => new() { Abilities = abilities };

    [Fact]
    public void IsRollSpell_TrueForCode145WithZeroValue()
        => Assert.True(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(145, 0))));

    [Fact]
    public void IsRollSpell_FalseForFixedRegenBonus()
        // AbilVal != 0 is a flat +N regen buff, not a roll — no reroll.
        => Assert.False(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(145, 12))));

    [Fact]
    public void IsRollSpell_FalseForManaHotCodes()
        // Chaos surge (heal-mana / HP-regen codes, no 145) recasts on expiry.
        => Assert.False(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(150, 0), new SpellAbility(123, 0))));

    [Fact]
    public void IsRollSpell_TrueWhenRollSlotSitsAmongOthers()
        => Assert.True(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(7, 3), new SpellAbility(145, 0))));

    // ----- Stock (tick-monitor) path -------------------------------------

    [Fact]
    public void StockLandingArmsTickWaitAndNeverQueriesAbil()
    {
        Harness h = new() { UseTickMonitor = true };

        h.Reroller.OnRollSpellLanded("ntap");

        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(0, h.AbilQueries);   // no abil 145 on Stock
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void StockTickAtOrAboveThresholdAccepts()
    {
        Harness h = new() { UseTickMonitor = true };   // threshold 5

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.OnManaTickObserved(6);

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void StockTickBelowThresholdRerolls()
    {
        Harness h = new() { UseTickMonitor = true };

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.OnManaTickObserved(3);

        Assert.Equal(new[] { "ntap" }, h.Recasts);
        Assert.True(h.Reroller.CycleActive);
    }

    [Fact]
    public void StockTickIgnoredWhenNoCycleAwaiting()
    {
        Harness h = new() { UseTickMonitor = true };

        // A tick with no roll-spell landing in flight is a normal regen tick — ignored.
        h.Reroller.OnManaTickObserved(2);

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void StockOnlyFirstTickPerLandingIsJudged()
    {
        Harness h = new() { UseTickMonitor = true };

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.OnManaTickObserved(6);   // accepted → cycle closes
        h.Reroller.OnManaTickObserved(1);   // a later low tick must not re-open / reroll

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    // ----- Unlimited ("reroll infinite") --------------------------------

    [Fact]
    public void UnlimitedKeepsRerollingPastAFiniteCap()
    {
        // Same always-bad-roll setup as RerollsUpToCapThenAcceptsWhateverLanded, but
        // Unlimited ignores the cap — it never accepts a below-threshold roll, so the
        // only thing that stops it here is our loop guard.
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 5, Cap: 3, Unlimited: true) };

        h.Reroller.OnRollSpellLanded("flux");
        int guard = 0;
        while (h.Reroller.CycleActive && guard++ < 10)
        {
            h.FeedRoll(1);
            if (h.Reroller.CycleActive) h.Reroller.OnRollSpellLanded("flux");
        }

        Assert.Equal(10, h.Recasts.Count);          // blew past cap 3 — still rerolling
        Assert.True(h.Reroller.CycleActive);
    }

    // ----- ReconsiderActiveRoll (reroll the buff that's already up) ------

    // Drive a cap-0 cycle so a bad roll is remembered in LastObservedValue with the
    // cycle closed — the state a config bump acts on.
    private static Harness WithAcceptedBadRoll(int roll)
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 0) };
        h.Reroller.OnRollSpellLanded("flux");
        h.FeedRoll(roll);                            // cap 0 → accepted immediately
        Assert.False(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);
        return h;
    }

    [Fact]
    public void ReconsiderActiveRoll_StagesRerollAfterCapRaised()
    {
        Harness h = WithAcceptedBadRoll(-2);         // -2 < threshold 0, remembered
        Assert.Equal(-2, h.Reroller.LastObservedValue);

        h.Config = h.Config with { Cap = 20 };       // user bumps 0 → 20
        h.Reroller.ReconsiderActiveRoll("flux");

        Assert.Equal(new[] { "flux" }, h.Recasts);   // rerolled the live -2
        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(1, h.Reroller.RerollsUsed);
    }

    [Fact]
    public void ReconsiderActiveRoll_InfiniteRerollsEvenWithCapZero()
    {
        Harness h = WithAcceptedBadRoll(-2);
        h.Config = h.Config with { Unlimited = true };   // infinite on, cap still 0
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhenActiveRollAlreadyClearsThreshold()
    {
        Harness h = WithAcceptedBadRoll(5);          // 5 >= threshold 0 — a good roll
        h.Config = h.Config with { Cap = 20 };
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // nothing to improve
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhenNoRollObservedYet()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 20) };
        h.Reroller.ReconsiderActiveRoll("flux");     // never saw a roll
        Assert.Empty(h.Recasts);
        Assert.Null(h.Reroller.LastObservedValue);
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhileACycleIsAlreadyInFlight()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 5, Cap: 3) };
        h.Reroller.OnRollSpellLanded("flux");        // cycle open, awaiting the abil read
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // don't double-drive an in-flight cycle
    }

    [Fact]
    public void ReconsiderActiveRoll_SuspendsAtManaFloorThenResumes()
    {
        Harness h = WithAcceptedBadRoll(-2);
        h.Config = h.Config with { Cap = 20 };
        h.CanAfford = false;

        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // can't pay yet
        Assert.True(h.Reroller.WaitingForMana);

        h.CanAfford = true;
        h.Reroller.OnRecoveryTick();
        Assert.Equal(new[] { "flux" }, h.Recasts);   // fired once mana recovered
    }
}
