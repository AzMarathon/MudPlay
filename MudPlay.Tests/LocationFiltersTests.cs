using System.Collections.Generic;
using System.Linq;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The Monsters tab's Landmass → Region → Area dropdowns narrow each other: a landmass offers
// only its regions, a region only its areas, and "(not set)" appears where monsters still
// have no label. The picks stay pending until the panel's Apply commits them.
public sealed class LocationFiltersTests
{
    private static readonly (string, string, string)[] Places =
    {
        ("Mainland", "Volcano", "Infernal Cavern"),
        ("Mainland", "Volcano", "Lava Pit"),
        ("Mainland", "Ocean", "Lagoon"),
        ("Albion", "Kingsport", "Castle"),
        ("Albion", "Kingsport", "Harbor"),
        ("Albion", "Vale of Durand", "Old Mill"),
        ("", "", ""),                       // a monster nobody has filed yet
    };

    private static LocationFilters Loaded()
    {
        LocationFilters f = new();
        f.Load(Places);
        return f;
    }

    [Fact]
    public void Start_OffersEveryLabel_AndNotSet_WhenSomeMonsterHasNone()
    {
        LocationFilters f = Loaded();

        Assert.Equal(new[] { "(any)", "(not set)", "Albion", "Mainland" }, f.Landmass.Options);
        Assert.Equal(new[] { "(any)", "(not set)", "Kingsport", "Ocean", "Vale of Durand", "Volcano" }, f.Region.Options);
        Assert.Contains("Castle", f.Area.Options);
        Assert.Contains("Infernal Cavern", f.Area.Options);
    }

    [Fact]
    public void NoBlankPlaces_OmitsNotSet()
    {
        LocationFilters f = new();
        f.Load(Places.Where(p => p.Item1.Length > 0));

        Assert.DoesNotContain(CategoryFilter.NotSetOption, f.Landmass.Options);
        Assert.DoesNotContain(CategoryFilter.NotSetOption, f.Region.Options);
        Assert.DoesNotContain(CategoryFilter.NotSetOption, f.Area.Options);
    }

    [Fact]
    public void PickingALandmass_NarrowsRegionsAndAreas()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Albion";

        Assert.Equal(new[] { "(any)", "Kingsport", "Vale of Durand" }, f.Region.Options);
        Assert.Equal(new[] { "(any)", "Castle", "Harbor", "Old Mill" }, f.Area.Options);
    }

    [Fact]
    public void PickingARegion_NarrowsAreasOnly()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Mainland";
        f.Region.Selected = "Volcano";

        Assert.Equal(new[] { "(any)", "Infernal Cavern", "Lava Pit" }, f.Area.Options);
        Assert.Equal("Mainland", f.Landmass.Selected);      // the pick above is untouched
    }

    [Fact]
    public void ChangingTheLandmass_DropsARegionItDoesNotHave()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Mainland";
        f.Region.Selected = "Volcano";
        f.Area.Selected = "Lava Pit";

        f.Landmass.Selected = "Albion";                      // Volcano is not on Albion

        Assert.Equal(CategoryFilter.AnyOption, f.Region.Selected);
        Assert.Equal(CategoryFilter.AnyOption, f.Area.Selected);
    }

    [Fact]
    public void ChangingTheLandmass_KeepsARegionThatIsStillOffered()
    {
        LocationFilters f = new();
        f.Load(new[] { ("Mainland", "Shared", "A"), ("Albion", "Shared", "B") });
        f.Landmass.Selected = "Mainland";
        f.Region.Selected = "Shared";

        f.Landmass.Selected = "Albion";

        Assert.Equal("Shared", f.Region.Selected);
    }

    [Fact]
    public void NotSetLandmass_OffersOnlyTheBlankPlaces()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = CategoryFilter.NotSetOption;

        Assert.Equal(new[] { "(any)", "(not set)" }, f.Region.Options);
    }

    [Fact]
    public void Reload_KeepsPicksThatStillExist_AndOffersNewLabels()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Albion";
        f.Region.Selected = "Kingsport";

        f.Load(Places.Append(("Albion", "Kingsport", "Docks")));   // the user filed a monster under a new area

        Assert.Equal("Albion", f.Landmass.Selected);
        Assert.Equal("Kingsport", f.Region.Selected);
        Assert.Contains("Docks", f.Area.Options);
    }

    [Fact]
    public void Reload_DropsAPickWhoseLabelVanished()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Albion";
        f.Region.Selected = "Vale of Durand";

        f.Load(Places.Where(p => p.Item2 != "Vale of Durand"));

        Assert.Equal("Albion", f.Landmass.Selected);
        Assert.Equal(CategoryFilter.AnyOption, f.Region.Selected);
    }

    [Fact]
    public void PicksApplyOnlyAfterCommit_AndMatchTheCell()
    {
        LocationFilters f = Loaded();
        f.Region.Selected = "Volcano";

        Assert.True(f.Region.Passes("Ocean"));              // still pending: nothing filtered yet
        f.Region.Commit();
        Assert.True(f.Region.Passes("Volcano"));
        Assert.True(f.Region.Passes("VOLCANO"));
        Assert.False(f.Region.Passes("Ocean"));
        Assert.False(f.Region.Passes(null));
    }

    [Fact]
    public void NotSet_MatchesBlankCellsOnly()
    {
        LocationFilters f = Loaded();
        f.Area.Selected = CategoryFilter.NotSetOption;
        f.Area.Commit();

        Assert.True(f.Area.Passes(null));
        Assert.True(f.Area.Passes(string.Empty));
        Assert.False(f.Area.Passes("Castle"));
    }

    [Fact]
    public void Clear_ResetsEveryDropdownToAny()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = "Mainland";
        f.Region.Selected = "Volcano";
        f.Area.Selected = "Lava Pit";

        foreach (CategoryFilter c in f.All) c.Clear();

        Assert.All(f.All, c => Assert.Equal(CategoryFilter.AnyOption, c.Selected));
        Assert.Contains("Albion", f.Landmass.Options);
        Assert.Contains("Kingsport", f.Region.Options);           // lists widened again
    }

    [Fact]
    public void ANullSelectionFromTheDropdown_MeansAny()
    {
        LocationFilters f = Loaded();
        f.Landmass.Selected = null!;                              // what a ComboBox writes back when its items are swapped

        Assert.Equal(CategoryFilter.AnyOption, f.Landmass.Selected);
    }

    [Fact]
    public void ExistingCategoryFilters_StillBehaveAsBefore()
    {
        CategoryFilter c = new("Type", "Type", new List<string> { "(any)", "Solo", "Undead" });
        c.Selected = "Undead";
        c.Commit();

        Assert.True(c.IsActive);
        Assert.True(c.Passes("undead"));
        Assert.False(c.Passes("Solo"));
        Assert.False(c.Passes(null));
    }
}
