using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.GameData.Edit;

// One "Room command" entry in an item's read-only game-data info: a command that,
// typed in any of the listed rooms, can hand the item over ("pry coffin — 18.6%").
// Not a floor placement, so it has its own group rather than sitting under
// "Placed in".
//
// The room list can run to a couple of hundred (every coffin room), so it opens
// showing the first few, with a toggle for the rest.
public sealed partial class RoomCommandRow : ObservableObject
{
    private const int ShownWhenCollapsed = 6;

    private readonly IReadOnlyList<PlacedInRow> _rooms;

    // The commands, alternatives joined with " / ", and the chance when there is one.
    public string Header { get; }

    public ObservableCollection<PlacedInRow> ShownRooms { get; } = new();

    // Every room of the entry, shown or not.
    public IReadOnlyList<PlacedInRow> Rooms => _rooms;

    public bool CanExpand => _rooms.Count > ShownWhenCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isExpanded;

    public string ToggleLabel => IsExpanded
        ? "Show fewer"
        : $"Show all {_rooms.Count} rooms";

    public RoomCommandRow(IReadOnlyList<string> commands, string chance, IReadOnlyList<PlacedInRow> rooms)
    {
        _rooms = rooms;
        string typed = string.Join(" / ", commands);
        Header = chance.Length == 0 ? typed : $"{typed} — {chance}";
        Show();
    }

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        Show();
    }

    private void Show()
    {
        ShownRooms.Clear();
        int count = IsExpanded ? _rooms.Count : Math.Min(_rooms.Count, ShownWhenCollapsed);
        for (int i = 0; i < count; i++) ShownRooms.Add(_rooms[i]);
    }
}
