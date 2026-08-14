using System;

namespace MudPlay.ViewModels.Navigation;

// Shared helper for the GOTO / loop filter boxes: does the filter text read as
// the word "favourite" / "favorite"? When it does, the filters additionally
// surface favourited rows (starred gotos, favourited loops/lairs) so typing
// "fav" / "favorite" reveals your favourites — not just literal name matches.
//
// Requires at least 3 characters so a single common letter ("a", "e") that
// happens to sit inside "favourite" doesn't drag every favourite into view.
internal static class FavoriteFilter
{
    public static bool IsFavoriteQuery(string filter)
        => filter.Length >= 3
        && ("favourite".Contains(filter, StringComparison.OrdinalIgnoreCase)
            || "favorite".Contains(filter, StringComparison.OrdinalIgnoreCase));
}
