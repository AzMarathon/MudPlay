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
}
