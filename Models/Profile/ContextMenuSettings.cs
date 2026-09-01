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

    // Stable id resolved against Services.MenuActionCatalogue (Entry kind). null
    // for a Separator or a Folder.
    public string? Id { get; set; }

    // Entry kind: optional user-chosen display name (null/empty = catalogue
    // default). Folder kind: the folder's name (shown as a fly-out submenu).
    public string? Label { get; set; }

    // Folder kind only: the entries shown when the folder flies out. One level
    // deep — a folder holds commands / links / separators, not other folders.
    public List<ContextMenuEntry>? Children { get; set; }
}

public enum ContextMenuEntryKind
{
    Entry,
    Separator,
    // A user-defined named submenu (folder) that flies out its Children.
    Folder,
}
