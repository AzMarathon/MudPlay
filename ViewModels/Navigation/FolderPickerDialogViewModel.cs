using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Pick a destination folder from a list instead of typing a path. Used by the
// "Move to folder…" action for a GOTO favourite. Returns the chosen folder path
// on Save ("" = move to root), null on Cancel. The caller applies it through
// FavoritesStore.MoveFavorite.
public sealed partial class FolderPickerDialogViewModel : ObservableObject, IDialogViewModel<string?>
{
    public event Action<string?>? CloseRequested;

    public FolderPickerDialogViewModel(IEnumerable<string> folders, string? current = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        Folders = new ObservableCollection<FolderChoice>
        {
            // Root option first — the way to move a favourite OUT of a folder.
            new("(No folder — root)", string.Empty),
        };
        foreach (string f in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            Folders.Add(new FolderChoice(f, f));

        string cur = NavFolders.Normalize(current);
        _selected = Folders.FirstOrDefault(c =>
                        string.Equals(c.Path, cur, StringComparison.OrdinalIgnoreCase))
                    ?? Folders[0];
    }

    public ObservableCollection<FolderChoice> Folders { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private FolderChoice? _selected;

    private bool CanSave => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(Selected?.Path ?? string.Empty);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// One selectable folder — Display is what the list shows, Path is the value
// returned (empty = root).
public sealed record FolderChoice(string Display, string Path);
