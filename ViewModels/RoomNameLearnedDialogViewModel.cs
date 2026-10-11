using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Yes/No prompt that fires when the room tracker learns a real name for a
// previously-unnamed room (typical of map-15 ganghouse rooms in 1.x MDB
// exports). On Save the name is kept with the realm's own data
// (LearnedRoomNameStore) and put back on the map each session; the game data
// files are never written. On Skip the in-memory rename is kept for the
// session but nothing is stored.
public sealed partial class RoomNameLearnedDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public RoomKey Key { get; }
    public string ObservedName { get; }

    public RoomNameLearnedDialogViewModel(RoomKey key, string observedName)
    {
        Key = key;
        ObservedName = observedName;
    }

    public string HeaderText =>
        $"Save the name seen for room {Key}?";

    public string DataNameLabel     => "Name in the game data:";
    public string ObservedNameLabel => "Name seen in the game:";
    public string DataNameValue     => "(none)";
    public string ObservedNameValue => ObservedName;

    public string WhereKeptText =>
        "Save keeps the name with this realm's own data and puts it back on the map every session. "
        + "The game data files are not changed. Skip leaves it on the map for this session only.";

    [RelayCommand] private void Save() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Skip() => CloseRequested?.Invoke(false);
}
