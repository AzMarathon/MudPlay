using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;

namespace MudPlay.ViewModels.GameData.Edit;

// Per-side edit dialog for Game Data Browser → Doors: sets whether a bare `(Key: N)`
// lock is a door or gate (bashes), a key exit (doesn't), or not known, or puts it back
// to the seed. Save returns the kind chosen; Reset to seed returns the reset; Cancel
// returns null.
public sealed partial class DoorEditDialogViewModel : ObservableObject, IDialogViewModel<DoorEdit>
{
    public event Action<DoorEdit?>? CloseRequested;

    public string Title { get; }

    // "Room 1/1790 (Dark Passage), west; key: green key".
    public string SideText { get; }

    // What the seed and the realm's record say, for the user to weigh the edit by.
    public string SeedText { get; }
    public string RecordText { get; }

    [ObservableProperty] private bool _isDoor;
    [ObservableProperty] private bool _isKeyExit;
    [ObservableProperty] private bool _isUnknown;

    public DoorEditDialogViewModel(DoorsRowInfo side)
    {
        ArgumentNullException.ThrowIfNull(side);
        Title = $"Door — {BareKeyLockBook.FormatSide(side.Room, side.Dir)}";
        SideText = $"Room {side.Room}{(string.IsNullOrWhiteSpace(side.RoomName) ? "" : $" ({side.RoomName})")}, "
            + $"{side.Dir.ToLongName()}; key: {side.KeyName}";
        SeedText = side.Seed switch
        {
            true => "Seed: a door or gate (bashes).",
            false => "Seed: a key exit (doesn't bash).",
            _ => "Seed: not in the seed.",
        };
        RecordText = side.Record is { } t
            ? $"This realm: answered {t.Door} time(s) as a door, {t.KeyExit} as a key exit"
              + (t.LastAnswered is { } at ? $", last {at:yyyy-MM-dd HH:mm}" : "")
              + (t.UserKind is { } u ? $"; set by you to {DoorsRowInfo.KindLabel(u)}." : ".")
            : "This realm: no answers yet.";
        IsDoor = side.Kind == BareLockBash.Bashable;
        IsKeyExit = side.Kind == BareLockBash.NotBashable;
        IsUnknown = side.Kind == BareLockBash.Unknown;
    }

    [RelayCommand]
    private void Save() => CloseRequested?.Invoke(new DoorEdit(
        IsDoor ? BareLockBash.Bashable : IsKeyExit ? BareLockBash.NotBashable : BareLockBash.Unknown,
        ResetToSeed: false));

    [RelayCommand]
    private void ResetToSeed() => CloseRequested?.Invoke(new DoorEdit(null, ResetToSeed: true));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
