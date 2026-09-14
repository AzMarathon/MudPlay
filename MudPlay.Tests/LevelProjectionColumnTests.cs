using System.Collections.Generic;
using System.Linq;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Level Projection column picker persists the HIDDEN set, not the selection.
// That choice is what lets a column added in a later release appear for a
// character whose save predates it — these pin that behaviour, plus the pinned
// identity column and the catalogue's own invariants.
public sealed class LevelProjectionColumnTests
{
    private static IReadOnlyList<string> Keys(IReadOnlyList<LevelProjectionColumn> cols)
        => cols.Select(c => c.Key).ToList();

    [Fact]
    public void NoSavedChoice_UsesTheDefaultSet()
    {
        IReadOnlyList<LevelProjectionColumn> resolved = LevelProjectionColumn.Resolve(null);
        Assert.Equal(LevelProjectionColumn.All.Where(c => c.DefaultVisible).Select(c => c.Key),
                     Keys(resolved));
    }

    [Fact]
    public void SavedHiddenKeys_AreDropped()
    {
        IReadOnlyList<LevelProjectionColumn> resolved =
            LevelProjectionColumn.Resolve(new[] { "crit", "dodge" });
        Assert.DoesNotContain("crit", Keys(resolved));
        Assert.DoesNotContain("dodge", Keys(resolved));
        Assert.Contains("stealth", Keys(resolved));
    }

    [Fact]
    public void ASaveThatPredatesAColumn_ShowsIt()
    {
        // An old save lists only the keys that existed then. A column added later
        // isn't in the hidden set, so it must surface rather than stay invisible.
        IReadOnlyList<LevelProjectionColumn> resolved =
            LevelProjectionColumn.Resolve(new[] { "totalxp" });
        Assert.Contains("thievery", Keys(resolved));
        Assert.Contains("perception", Keys(resolved));
        Assert.DoesNotContain("totalxp", Keys(resolved));
    }

    [Fact]
    public void HidingEverything_StillLeavesTheLevelColumn()
    {
        IReadOnlyList<LevelProjectionColumn> resolved =
            LevelProjectionColumn.Resolve(LevelProjectionColumn.All.Select(c => c.Key).ToList());
        Assert.Equal(new[] { LevelProjectionColumn.PinnedKey }, Keys(resolved));
    }

    [Fact]
    public void ResolvePreservesCatalogueOrder()
    {
        IReadOnlyList<string> resolved = Keys(LevelProjectionColumn.Resolve(new[] { "hp", "mana" }));
        IReadOnlyList<string> expectedOrder = LevelProjectionColumn.All
            .Select(c => c.Key)
            .Where(k => k is not ("hp" or "mana"))
            .ToList();
        Assert.Equal(expectedOrder, resolved);
    }

    [Fact]
    public void CatalogueKeysAndBindingsAreUnique()
    {
        // A duplicate key would make two columns toggle as one; a duplicate binding
        // would render the same value twice under different headers.
        Assert.Equal(LevelProjectionColumn.All.Count,
                     LevelProjectionColumn.All.Select(c => c.Key).Distinct().Count());
        Assert.Equal(LevelProjectionColumn.All.Count,
                     LevelProjectionColumn.All.Select(c => c.Binding).Distinct().Count());
    }

    [Fact]
    public void EveryBindingResolvesToARowProperty()
    {
        // The view binds by property name, so a typo in the catalogue would show an
        // empty column at runtime instead of failing to compile.
        foreach (LevelProjectionColumn col in LevelProjectionColumn.All)
            Assert.NotNull(typeof(LevelProjectionRow).GetProperty(col.Binding));
    }

    [Fact]
    public void PinnedColumnIsInTheCatalogueAndDefaultsVisible()
    {
        LevelProjectionColumn pinned =
            Assert.Single(LevelProjectionColumn.All.Where(c => c.Key == LevelProjectionColumn.PinnedKey));
        Assert.True(pinned.DefaultVisible);
    }
}
