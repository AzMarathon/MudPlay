using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Yes/No prompt that fires when the room tracker learns a name for a room the
// game data leaves unnamed (typical of map-15 ganghouse rooms in 1.x MDB
// exports): the first name seen for it, or another than the one kept before, a
// board being free to rename such a room. On Save the name is kept with the
// realm's own data (LearnedRoomNameStore), in place of any kept before, and put
// back on the map each session; the game data files are never written. On Skip
// the in-memory rename is kept for the session but nothing is stored.
public sealed partial class RoomNameLearnedDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public RoomKey Key { get; }
    public string ObservedName { get; }

    // The name on file for the room that Save would replace, or null.
    public string? KeptBefore { get; }

    public RoomNameLearnedDialogViewModel(RoomKey key, string observedName, string? keptBefore = null)
    {
        Key = key;
        ObservedName = observedName;
        KeptBefore = string.IsNullOrWhiteSpace(keptBefore)
            || string.Equals(keptBefore, observedName, StringComparison.Ordinal)
            ? null
            : keptBefore;
    }

    public bool HasKeptBefore => KeptBefore is not null;

    public string HeaderText => HasKeptBefore
        ? $"Room {Key} has another name now. Save it in place of the one kept?"
        : $"Save the name seen for room {Key}?";

    public string DataNameLabel     => "Name in the game data:";
    public string KeptBeforeLabel   => "Name kept before:";
    public string ObservedNameLabel => "Name seen in the game:";
    public string DataNameValue     => "(none)";
    public string KeptBeforeValue   => KeptBefore ?? string.Empty;
    public string ObservedNameValue => ObservedName;

    public string WhereKeptText => HasKeptBefore
        ? "Save replaces the name kept with this realm's own data and puts the new one on the map every session. "
          + "The game data files are not changed. Skip leaves the new name on the map for this session only; "
          + "the name kept before stays on file."
        : "Save keeps the name with this realm's own data and puts it back on the map every session. "
          + "The game data files are not changed. Skip leaves it on the map for this session only.";

    [RelayCommand] private void Save() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Skip() => CloseRequested?.Invoke(false);
}
