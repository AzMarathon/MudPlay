using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Which per-character room set a row belongs to. The two sets are independent
// flags (a room can be both avoided AND a stash), so a room present in both
// surfaces as two rows — one per kind.
public enum AvoidRoomKind
{
    Avoid,
    Stash,
}

// One row in the Modify-Avoid-Rooms editor: a (Kind, Map, Room, Name) tuple
// projected from MovementFilter's avoided / stash sets. Immutable — changing a
// room's kind is a remove-then-add, so a plain record with value equality is
// enough (and makes the ListBox multi-select dedup / Remove behave).
public sealed record AvoidRoomRow(AvoidRoomKind Kind, int Map, int Room, string Name)
{
    public string TypeLabel => Kind == AvoidRoomKind.Avoid ? "Avoid Room" : "Stash Room";
}

// Add-row type-dropdown option. ToString is the display caption so the ComboBox
// renders it without a template.
public sealed record AvoidRoomKindOption(AvoidRoomKind Kind, string Label)
{
    public override string ToString() => Label;
}

// Staged editor for the per-character avoided + stash room sets (both stored on
// CharacterProfile via MovementFilter). Surfaces both sets as one merged,
// type-tagged list. In-dialog Add / Remove mutate a local working copy; Save
// commits the working copy to the filter in one shot (persists + recolours the
// map); Cancel discards.
//
// Add flow mirrors the blacklist editor: the user picks a type, types a Map and
// a Room number, and as soon as both parse the dialog looks up the room in the
// active graph and pre-fills the Name preview. Add is enabled when the key
// resolves AND that (Kind, Map, Room) row isn't already listed.
public sealed partial class AvoidRoomsEditorDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly MovementFilter _filter;
    private readonly RoomGraphManager _graph;

    public ObservableCollection<AvoidRoomRow> Entries { get; } = new();

    // Live mirror of the list's multi-selection set — the dialog's code-behind
    // syncs this on EntriesList.SelectionChanged (Avalonia exposes SelectedItems
    // as a non-bindable IList, so the sync is imperative). RemoveSelected drops
    // every entry in this set so a Ctrl-/Shift-selection removes them all.
    public ObservableCollection<AvoidRoomRow> SelectedEntries { get; } = new();

    // Type dropdown options for the Add row.
    public IReadOnlyList<AvoidRoomKindOption> KindOptions { get; } = new[]
    {
        new AvoidRoomKindOption(AvoidRoomKind.Avoid, "Avoid Room"),
        new AvoidRoomKindOption(AvoidRoomKind.Stash, "Stash Room"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private AvoidRoomKindOption _addKind;

    // Map number input in the Add row (display string so empty stays empty).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddNamePreview))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _addMap = string.Empty;

    // Room number input in the Add row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddNamePreview))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _addRoom = string.Empty;

    public bool CanRemoveSelected => SelectedEntries.Count > 0;

    // True when both inputs parse + room exists + that (Kind, Map, Room) isn't
    // already listed.
    public bool CanAdd
    {
        get
        {
            if (!TryParseAddKey(out RoomKey key)) return false;
            if (_graph.GetRoom(key) is null) return false;
            AvoidRoomKind kind = AddKind.Kind;
            foreach (AvoidRoomRow e in Entries)
                if (e.Kind == kind && e.Map == key.Map && e.Room == key.Room) return false;
            return true;
        }
    }

    // Name preview for the Add row — reads the active set's Rooms.json via
    // RoomGraphManager.GetRoom.
    public string AddNamePreview
    {
        get
        {
            if (!TryParseAddKey(out RoomKey key)) return "(enter map and room number)";
            Room? r = _graph.GetRoom(key);
            if (r is null) return $"(no room at {key} in this game-data set)";
            return r.DisplayName;
        }
    }

    public AvoidRoomsEditorDialogViewModel(MovementFilter filter, RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);
        _filter = filter;
        _graph = graph;
        _addKind = KindOptions[0];

        // Snapshot the filter's current sets into the merged working copy,
        // naming each room from the live graph (falls back to the raw key when
        // the room isn't in the active set).
        foreach (RoomKey k in _filter.Avoided)
            Entries.Add(new AvoidRoomRow(AvoidRoomKind.Avoid, k.Map, k.Room, NameFor(k)));
        foreach (RoomKey k in _filter.Stash)
            Entries.Add(new AvoidRoomRow(AvoidRoomKind.Stash, k.Map, k.Room, NameFor(k)));

        ApplySort();

        SelectedEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanRemoveSelected));
    }

    private string NameFor(RoomKey key) => _graph.GetRoom(key)?.DisplayName ?? "???";

    // ----- Column sorting ------------------------------------------------
    // The entries list is a plain ListBox, so column sorting is driven by hand:
    // clicking a header re-orders Entries in place and toggles asc/desc on a
    // repeat click of the same column.
    private string _sortColumn = "Type";
    private bool _sortAscending = true;

    public string TypeHeader => HeaderText("Type", "Type");
    public string MapHeader  => HeaderText("Map",  "Map");
    public string RoomHeader => HeaderText("Room", "Room");
    public string NameHeader => HeaderText("Name", "Name");

    private string HeaderText(string column, string label)
        => _sortColumn == column ? $"{label} {(_sortAscending ? "▲" : "▼")}" : label;

    [RelayCommand]
    private void Sort(string? column)
    {
        if (string.IsNullOrEmpty(column)) return;
        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else { _sortColumn = column; _sortAscending = true; }
        ApplySort();
        OnPropertyChanged(nameof(TypeHeader));
        OnPropertyChanged(nameof(MapHeader));
        OnPropertyChanged(nameof(RoomHeader));
        OnPropertyChanged(nameof(NameHeader));
    }

    // Re-order Entries per the active column + direction, keeping (Map, Room) as
    // the tiebreaker so rooms group by map within a type.
    private void ApplySort()
    {
        List<AvoidRoomRow> sorted = _sortColumn switch
        {
            "Map" => _sortAscending
                ? Entries.OrderBy(e => e.Map).ThenBy(e => e.Room).ToList()
                : Entries.OrderByDescending(e => e.Map).ThenByDescending(e => e.Room).ToList(),
            "Room" => _sortAscending
                ? Entries.OrderBy(e => e.Room).ThenBy(e => e.Map).ToList()
                : Entries.OrderByDescending(e => e.Room).ThenByDescending(e => e.Map).ToList(),
            "Name" => _sortAscending
                ? Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : Entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => _sortAscending
                ? Entries.OrderBy(e => e.TypeLabel, StringComparer.Ordinal).ThenBy(e => e.Map).ThenBy(e => e.Room).ToList()
                : Entries.OrderByDescending(e => e.TypeLabel, StringComparer.Ordinal).ThenBy(e => e.Map).ThenBy(e => e.Room).ToList(),
        };

        Entries.Clear();
        foreach (AvoidRoomRow e in sorted) Entries.Add(e);
    }

    [RelayCommand]
    private void AddRow()
    {
        if (!CanAdd) return;
        if (!TryParseAddKey(out RoomKey key)) return;
        Entries.Add(new AvoidRoomRow(AddKind.Kind, key.Map, key.Room, NameFor(key)));
        AddMap = string.Empty;
        AddRoom = string.Empty;
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddNamePreview));
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedEntries.Count == 0) return;
        // Snapshot first: removing from Entries re-fires the control's
        // SelectionChanged, which clears + rebuilds SelectedEntries mid-loop.
        foreach (AvoidRoomRow sel in SelectedEntries.ToList())
            Entries.Remove(sel);
        SelectedEntries.Clear();
        OnPropertyChanged(nameof(CanAdd));               // free-up the tuples for re-add
    }

    [RelayCommand]
    private void Save()
    {
        List<RoomKey> avoided = Entries
            .Where(e => e.Kind == AvoidRoomKind.Avoid)
            .Select(e => new RoomKey(e.Map, e.Room))
            .ToList();
        List<RoomKey> stash = Entries
            .Where(e => e.Kind == AvoidRoomKind.Stash)
            .Select(e => new RoomKey(e.Map, e.Room))
            .ToList();
        _filter.ReplaceAll(avoided, stash);              // persists + fires Changed → map redraws
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private bool TryParseAddKey(out RoomKey key)
    {
        key = default;
        if (!int.TryParse(AddMap, out int m)  || m <= 0) return false;
        if (!int.TryParse(AddRoom, out int r) || r <= 0) return false;
        key = new RoomKey(m, r);
        return true;
    }
}
