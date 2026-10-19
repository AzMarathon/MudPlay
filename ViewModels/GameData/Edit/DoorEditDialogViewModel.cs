using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;

namespace MudPlay.ViewModels.GameData.Edit;

// Per-side edit dialog for Game Data Browser → Doors: sets whether a bare `(Key: N)`
// lock is a door or gate (bashes), a key exit (doesn't), or not known, or puts it back
// to the seed. Save returns the kind chosen, and is offered only once the choice
// differs from what the side reads as, so opening and saving pins nothing; Reset to
// seed returns the reset; Cancel returns null. While it is open, a change to the side
// (an answer learned, another client's edit) is shown in StaleNote.
public sealed partial class DoorEditDialogViewModel : ObservableObject, IDialogViewModel<DoorEdit>, IDisposable
{
    public event Action<DoorEdit?>? CloseRequested;

    public string Title { get; }

    // "Room 1/1790 (Dark Passage), west; key: green key".
    public string SideText { get; }

    // What the seed and the realm's record say, for the user to weigh the edit by.
    public string SeedText { get; }
    public string RecordText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDoor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isKeyExit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isUnknown;

    // "This side changed since you opened it: now …", or empty. Read live off the
    // book; its change is told on the UI thread.
    public string StaleNote =>
        _book is null || (_book.RecordOf(_side.Room, _side.Dir) == _side.Record && _book.Lookup(_side.Room, _side.Dir) == _side.Kind)
            ? string.Empty
            : $"This side changed since you opened it: now {_book.Describe(_side.Room, _side.Dir)}.";

    private readonly DoorsRowInfo _side;
    private readonly BareKeyLockBook? _book;

    public BareLockBash Chosen =>
        IsDoor ? BareLockBash.Bashable : IsKeyExit ? BareLockBash.NotBashable : BareLockBash.Unknown;

    public bool CanSave => Chosen != _side.Kind;

    // book: the live record, watched for changes to the side while the dialog is open.
    public DoorEditDialogViewModel(DoorsRowInfo side, BareKeyLockBook? book = null)
    {
        ArgumentNullException.ThrowIfNull(side);
        _side = side;
        _book = book;
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
        if (_book is not null) _book.Changed += OnBookChanged;
    }

    private void OnBookChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) OnPropertyChanged(nameof(StaleNote));
        else Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(StaleNote)));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave) return;
        Close(new DoorEdit(Chosen, ResetToSeed: false));
    }

    [RelayCommand]
    private void ResetToSeed() => Close(new DoorEdit(null, ResetToSeed: true));

    [RelayCommand]
    private void Cancel() => Close(null);

    private void Close(DoorEdit? edit)
    {
        Dispose();
        CloseRequested?.Invoke(edit);
    }

    // The book outlives the dialog: the window drops the watch when it closes, the
    // title-bar X included, which the view-model never sees.
    public void Dispose()
    {
        if (_book is not null) _book.Changed -= OnBookChanged;
    }
}
