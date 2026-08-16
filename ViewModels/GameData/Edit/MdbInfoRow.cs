using System.Collections.Generic;

namespace MudPlay.ViewModels.GameData.Edit;

// One label/value row in a monster's read-only "Other Info (from MDB)" pane.
// Most rows are plain text; a row backed by a TBInfo textblock (currently the
// Greet row) also carries the command keywords that block responds to, printed
// inline under the value as an indented outline. A room-list row (Spawns In /
// Placed In / Summoned In) carries clickable per-room chips that open the
// room-detail popup; an item-list row (Item Drops) carries clickable per-item
// chips that jump to the item's Items-tab record. A FullWidth row (an attack's
// name header) spans both columns so a long name wraps instead of being clipped
// in the narrow key column, with its stat sub-rows following. Actions / Rooms /
// Items are null for plain rows. Key/Value are named to match the previous
// KeyValuePair binding so the existing template markup keeps working for plain rows.
public sealed record MdbInfoRow(
    string Key,
    string Value,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<RoomLink>? Rooms = null,
    IReadOnlyList<ItemLink>? Items = null,
    bool FullWidth = false)
{
    // View binds this to print the textblock's keyword/effect outline inline
    // under the value.
    public bool HasActions => Actions is { Count: > 0 };

    // View binds this to render the value as a wrap of clickable room chips.
    public bool HasRooms => Rooms is { Count: > 0 };

    // View binds this to render the value as a wrap of clickable item chips.
    public bool HasItems => Items is { Count: > 0 };

    // A FullWidth row renders its Key across both grid columns (wrapping), so the
    // normal key cell + every value branch below stay hidden for it.
    public bool ShowKeyCell => !FullWidth;

    // The plain-text branch shows only when no rich branch applies — compiled
    // bindings can't express the whole negation inline.
    public bool IsPlain => !HasActions && !HasRooms && !HasItems && !FullWidth;
}
