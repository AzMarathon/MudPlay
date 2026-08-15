using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The auto-engine base-modes reconcile clones the base onto the live AutoMode and
// skips the write when they already match, so Clone must be an independent copy
// and SameAs must compare every engine flag.
public sealed class AutoActionDefaultsTests
{
    [Fact]
    public void Clone_CopiesEveryFlag_Independently()
    {
        AutoActionDefaults src = new()
        {
            AutoCombat = false, AutoNuke = true, AutoHealRest = false,
            AutoBless = true, AutoLight = true, AutoGetItems = false,
            AutoGetCash = true, AutoSneak = false, AutoHide = true, AutoSearch = true,
        };

        AutoActionDefaults copy = src.Clone();
        Assert.True(copy.SameAs(src));

        // Mutating the clone must not reach back into the source.
        copy.AutoCombat = true;
        Assert.False(copy.SameAs(src));
        Assert.False(src.AutoCombat);
    }

    [Fact]
    public void SameAs_TrueOnlyWhenEveryFlagMatches()
    {
        AutoActionDefaults a = new();       // factory defaults
        AutoActionDefaults b = a.Clone();
        Assert.True(a.SameAs(b));

        b.AutoSearch = !b.AutoSearch;       // flip a single flag
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SameAs_False_ForNull()
    {
        AutoActionDefaults a = new();
        Assert.False(a.SameAs(null!));
    }

    // ----- ReconcileToBase (base-modes-apply-on-load) -----

    [Fact]
    public void ReconcileToBase_NullBase_SeedsBaseFromLive_LeavesLiveUnchanged()
    {
        // Legacy profile: no base yet. It must adopt the current live modes as the
        // base (so the checkboxes become a concrete default) without changing live.
        AutoActionDefaults live = new() { AutoCombat = true, AutoSneak = false, AutoHide = true };

        AutoModeReconcileResult r = AutoActionDefaults.ReconcileToBase(null, live);

        Assert.True(r.BaseSeeded);
        Assert.False(r.LiveChanged);
        Assert.True(r.Base.SameAs(live));   // base captured from live
        Assert.True(r.Live.SameAs(live));   // live untouched
        // Independent copies — mutating a result must not reach into the input.
        r.Base.AutoCombat = false;
        Assert.True(live.AutoCombat);
    }

    [Fact]
    public void ReconcileToBase_BaseDiffersFromLive_SettlesLiveToBase()
    {
        // The user flipped combat off on the toolbar mid-route (live) but their base
        // keeps it on — a load must settle live back to the base.
        AutoActionDefaults baseModes = new() { AutoCombat = true,  AutoBless = true };
        AutoActionDefaults live      = new() { AutoCombat = false, AutoBless = true };

        AutoModeReconcileResult r = AutoActionDefaults.ReconcileToBase(baseModes, live);

        Assert.False(r.BaseSeeded);
        Assert.True(r.LiveChanged);
        Assert.True(r.Live.SameAs(baseModes));   // live now matches base
        Assert.True(r.Base.SameAs(baseModes));   // base preserved
    }

    [Fact]
    public void ReconcileToBase_BaseEqualsLive_IsNoOp()
    {
        AutoActionDefaults baseModes = new() { AutoCombat = true, AutoNuke = false };
        AutoActionDefaults live      = baseModes.Clone();

        AutoModeReconcileResult r = AutoActionDefaults.ReconcileToBase(baseModes, live);

        Assert.False(r.BaseSeeded);
        Assert.False(r.LiveChanged);
    }
}
