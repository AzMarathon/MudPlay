using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Canonical default terminal right-click menu — the entries below the pinned
// Favorites / Recent-destinations walk flyouts. Seeded to exactly what the menu
// shipped with before it became customizable (panel quick-opens → Reset States
// → Bug report), so a profile with no stored ContextMenuSettings.Layout, and
// "Reset to defaults" in the editor, both reproduce the familiar menu. The
// catalogue (MenuActionCatalogue) is the pool of everything addable; this is
// only the default arrangement.
public static class ContextMenuDefaults
{
    // Entry ids in default order. null marks a separator.
    private static readonly string?[] _order =
    {
        "walk.favorites",
        "walk.recent",
        null,
        "view.backscroll",
        "view.workshop",
        "view.party",
        "view.spellbook",
        "view.monsterintel",
        "view.conversation",
        "view.navigation",
        "view.sessionstats",
        null,
        "action.resetstates",
        null,
        "tools.bugreport",
    };

    // Fresh list each call so the caller can mutate freely.
    public static List<ContextMenuEntry> Build()
    {
        List<ContextMenuEntry> list = new(_order.Length);
        foreach (string? id in _order)
        {
            list.Add(id is null
                ? new ContextMenuEntry { Kind = ContextMenuEntryKind.Separator }
                : new ContextMenuEntry { Kind = ContextMenuEntryKind.Entry, Id = id });
        }
        return list;
    }
}
