using System.Collections.Generic;

namespace MudPlay.ViewModels.GameData.Tables;

// One labelled section of a table's curation panel — a header ("Combat",
// "Defenses", …) over the range / checkbox / dropdown filters that belong to it.
// Grouping keeps a large facet set legible instead of one long flat stack.
// A subclass builds these; the base VM flattens them for the actual filtering.
public sealed class FilterGroup
{
    public string Header { get; }
    public IReadOnlyList<RangeFilter> Ranges { get; }
    public IReadOnlyList<BoolFilter> Bools { get; }
    public IReadOnlyList<CategoryFilter> Categories { get; }

    public FilterGroup(
        string header,
        IReadOnlyList<RangeFilter>? ranges = null,
        IReadOnlyList<BoolFilter>? bools = null,
        IReadOnlyList<CategoryFilter>? categories = null)
    {
        Header = header;
        Ranges = ranges ?? System.Array.Empty<RangeFilter>();
        Bools = bools ?? System.Array.Empty<BoolFilter>();
        Categories = categories ?? System.Array.Empty<CategoryFilter>();
    }

    public bool HasRanges => Ranges.Count > 0;
    public bool HasBools => Bools.Count > 0;
    public bool HasCategories => Categories.Count > 0;
}
