using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// One clickable "map/room" chip in a monster record's spawn/placed/summoned room
// lists. Clicking opens the Navigation map on that room and selects it, so its
// details show in the map's ROOM INFO panel — the monster pane doubles as a
// jump-off to the geography it references.
public sealed class RoomLink
{
    public string Label { get; }
    public ICommand Open { get; }

    public RoomLink(string label, RoomKey key)
    {
        Label = label;
        Open = new RelayCommand(() => AppServices.Current.NavigateToRoom(key));
    }
}
