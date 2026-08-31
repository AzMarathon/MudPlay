namespace MudPlay.Models.Profile;

// Per-character customization of the terminal right-click (context) menu — an
// ordered list the user arranges in Settings → Toolbar + Shortcuts, mirroring
// the toolbar-layout model (ToolbarSettings). Each entry is either a
// catalogue-resolved menu entry (a command, a toggle, a whole main-menu
// submenu, a Player Workshop tab link, or a calculator link — see
// Services.MenuActionCatalogue) or a separator. Persisted as the "ContextMenu"
// entry in CharacterProfile.Settings. null Layout means "use defaults"
// (Services.ContextMenuDefaults — the built-in menu).
//
// The pinned Favorites / Recent-destinations walk flyouts are NOT part of this
// list — they always lead the menu; this list is everything below them.
public sealed class ContextMenuSettings
{
    // Ordered entries. null or empty falls back to Services.ContextMenuDefaults.
    public List<ContextMenuEntry>? Layout { get; set; }
}

// One entry in the persisted context-menu layout.
public sealed class ContextMenuEntry
{
    public ContextMenuEntryKind Kind { get; set; } = ContextMenuEntryKind.Entry;

    // Stable id resolved against Services.MenuActionCatalogue. null when Kind is
    // ContextMenuEntryKind.Separator.
    public string? Id { get; set; }

    // Optional user-chosen display name — the entry still LINKS to Id's action /
    // submenu / tab / calculator, this just renames how it appears in the menu.
    // null / empty falls back to the catalogue entry's default label.
    public string? Label { get; set; }
}

public enum ContextMenuEntryKind
{
    Entry,
    Separator,
}
