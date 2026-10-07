using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// One "placed in" entry in an item's read-only MDB info: a room whose static
// Placed list drops this item onto the floor (surfaced on the item as
// "Obtained From: Room {map}/{room}"). The location is a clickable link to the
// room's Rooms-tab record plus a "Queue Walking here" action — mirrors the
// bought/sold shop rows, minus the price line (a floor item has no cost).
public sealed class PlacedInRow
{
    public string Location { get; }

    // False when the room coordinate couldn't be resolved (map/room <= 0) — the
    // row still shows the raw locator but has nothing to navigate to, so the view
    // greys the link.
    public bool CanOpen { get; }
    public ICommand Open { get; }
    public ICommand QueueWalk { get; }

    public PlacedInRow(string location, int map, int room)
    {
        Location = location;
        CanOpen = map > 0 && room > 0;
        Open = new RelayCommand(
            () => AppServices.Current.OpenRoomGameData(map, room),
            () => CanOpen);
        QueueWalk = new RelayCommand(
            () => AppServices.Current.QueueWalkTo(new RoomKey(map, room)),
            () => CanOpen);
    }
}
