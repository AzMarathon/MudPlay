using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Read-only "How did I Die?" viewer — shows the backscroll snapshot captured at
// the moment of a recorded death. No pending state, so there's only a Close
// path; the standard edit-window Save/Cancel contract doesn't apply.
public sealed partial class DeathLogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public string Title { get; }
    public string LogText { get; }

    public DeathLogViewModel(string title, string logText)
    {
        Title = title;
        LogText = logText;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);
}
