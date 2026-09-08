namespace MudPlay.Models.Profile;

// Per-character customization of the terminal right-click (context) menu — an
// ordered list the user arranges in Settings → Toolbar + Shortcuts, mirroring
// the toolbar-layout model (ToolbarSettings). Each entry is a separator, a
// user-defined fly-out folder, or a catalogue-resolved action — a command, a
// toggle, a Workshop-tab / calculator / Settings-tab / Game-Data-section link,
// or a walk fly-out (Favorites / Recent destinations); see the kinds on
// Services.MenuActionCatalogue.Entry and ContextMenuEntry below. Persisted as the
// "ContextMenu" entry in CharacterProfile.Settings; null Layout means "use
// defaults" (Services.ContextMenuDefaults — the built-in menu).
//
// The Favorites / Recent-destinations walk fly-outs are ordinary entries the user
// can reorder or remove, NOT pinned leaders — ContextMenuDefaults just seeds them
// first. They're single-instance, so the editor drops them from the pool once
// placed; nothing is fixed ahead of the user's list (MainWindow's
// ContextMenuFixedLeadingItems is 0).
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
