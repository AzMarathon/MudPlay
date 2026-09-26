using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace MudPlay.ViewModels.GameData.Tables;

// The Monsters tab's Location section: three dropdowns — Landmass, Region, Area — that
// narrow each other. Picking a landmass offers only the regions found under it, picking a
// region offers only its areas, so the lists stay short and a player can walk down to
// "Mainland → Volcano → Infernal Cavern". Each list also offers "(not set)" when some
// monsters in scope have no label yet, to find the ones still to be filed. The places
// come from the monsters' own resolved labels (shipped seed plus the user's overrides),
// so a label the user types into a record shows up here on the next reload.
public sealed class LocationFilters
{
    public CategoryFilter Landmass { get; } = new("Landmass", "Landmass", Blank(),
        "Only monsters on this landmass (Mainland, Albion, Shadowmere, …)");
    public CategoryFilter Region { get; } = new("Region", "Region", Blank(),
        "Only monsters in this region; the list follows the landmass picked above");
    public CategoryFilter Area { get; } = new("Area", "Area", Blank(),
        "Only monsters in this area; the list follows the landmass and region picked above");

    public IReadOnlyList<CategoryFilter> All { get; }

    private IReadOnlyList<(string Landmass, string Region, string Area)> _places = Array.Empty<(string, string, string)>();
    private bool _refreshing;

    public LocationFilters()
    {
        All = new[] { Landmass, Region, Area };
        Landmass.PropertyChanged += OnPicked;
        Region.PropertyChanged += OnPicked;
    }

    // Replace the places the lists are built from (one entry per monster; blank = not set).
    public void Load(IEnumerable<(string Landmass, string Region, string Area)> places)
    {
        _places = places.ToList();
        Rebuild();
    }

    private void OnPicked(object? sender, PropertyChangedEventArgs e)
    {
        if (_refreshing || e.PropertyName != nameof(CategoryFilter.Selected)) return;
        Rebuild();
    }

    // Rebuild the three lists top-down from the current picks. A pick that is no longer
    // offered falls back to "(any)" (SetOptions), which in turn widens the lists below it.
    private void Rebuild()
    {
        _refreshing = true;
        try
        {
            Landmass.SetOptions(Offer(_places.Select(p => p.Landmass)));
            var inLandmass = _places.Where(p => Matches(p.Landmass, Landmass.Selected)).ToList();
            Region.SetOptions(Offer(inLandmass.Select(p => p.Region)));
            var inRegion = inLandmass.Where(p => Matches(p.Region, Region.Selected));
            Area.SetOptions(Offer(inRegion.Select(p => p.Area)));
        }
        finally { _refreshing = false; }
    }

    private static bool Matches(string value, string pick) =>
        pick == CategoryFilter.AnyOption
        || (pick == CategoryFilter.NotSetOption
            ? string.IsNullOrEmpty(value)
            : string.Equals(value, pick, StringComparison.OrdinalIgnoreCase));

    // "(any)", then "(not set)" when some value is blank, then the distinct labels A–Z.
    private static IReadOnlyList<string> Offer(IEnumerable<string> values)
    {
        List<string> distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> options = new() { CategoryFilter.AnyOption };
        if (distinct.Any(string.IsNullOrEmpty)) options.Add(CategoryFilter.NotSetOption);
        options.AddRange(distinct.Where(v => !string.IsNullOrEmpty(v)).Order(StringComparer.OrdinalIgnoreCase));
        return options;
    }

    private static IReadOnlyList<string> Blank() => new[] { CategoryFilter.AnyOption };
}
