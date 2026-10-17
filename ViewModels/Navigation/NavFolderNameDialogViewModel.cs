using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Tiny single-text-box prompt for naming a navigation folder — shared by
// the "New folder" and "Rename folder" actions on both the Manage dialog
// (loops + lairs) and the rail (gotos). Returns the typed /-separated folder
// path on Save, null on Cancel. The caller normalises + applies the result
// via the relevant store / NavFolderManager.
public sealed partial class NavFolderNameDialogViewModel : ObservableObject, IDialogViewModel<string?>
{
    public event Action<string?>? CloseRequested;

    // Loop and lair folders are folders on disk, so what NavFolderManager would
    // refuse is said as the user types; GOTO folders live in the profile and pass
    // none.
    private readonly Func<string, string?>? _whyNot;

    public NavFolderNameDialogViewModel(string title, string prompt, string initial = "",
        Func<string, string?>? whyNot = null)
    {
        Title = title;
        Prompt = prompt;
        _whyNot = whyNot;
        _name = initial ?? string.Empty;
    }

    // Window title — e.g. "New folder" / "Rename folder".
    public string Title { get; }

    // One-line instruction shown above the input box.
    public string Prompt { get; }

    // The folder path being entered. /-separated; empty is rejected by the caller.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorMessage))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    public string ErrorMessage => _whyNot?.Invoke(Name?.Trim() ?? string.Empty) ?? string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(Name?.Trim() ?? string.Empty);

    private bool CanSave() => !HasError;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
