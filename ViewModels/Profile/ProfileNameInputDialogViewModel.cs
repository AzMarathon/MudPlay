using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.Profile;

// File → Save profile as dialog. Prompts the user for a profile name (which becomes the
// per-character folder under Data/profiles/). Inline validates the name against
// filesystem-unsafe characters and surfaces an overwrite warning when a folder with
// that name already exists; Save commits on Enter / button, Cancel / X returns null.
public sealed partial class ProfileNameInputDialogViewModel : ObservableObject, IDialogViewModel<string>
{
    public event Action<string?>? CloseRequested;

    private readonly Func<string, bool> _exists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalizedName))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ErrorMessage))]
    [NotifyPropertyChangedFor(nameof(WillOverwrite))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    public string NormalizedName => (Name ?? string.Empty).Trim();

    // Filesystem-invalid character names trip this; rendered red below the input.
    public bool HasError => !string.IsNullOrEmpty(ValidateName(NormalizedName));

    public string ErrorMessage => ValidateName(NormalizedName) ?? string.Empty;

    // True when the entered name already exists on disk — surfaces an inline "will overwrite" warning.
    public bool WillOverwrite => !HasError && NormalizedName.Length > 0 && _exists(NormalizedName);

    public bool CanSave => !HasError && NormalizedName.Length > 0;

    public ProfileNameInputDialogViewModel(string suggestedName, Func<string, bool> exists)
    {
        _exists = exists;
        Name    = suggestedName;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        CloseRequested?.Invoke(NormalizedName);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // The name becomes a folder's, so what is refused here is exactly what the
    // stores refuse (PathGuard): the user is told as they type, not after. An empty
    // box is no error yet; Save is simply not offered.
    private static string? ValidateName(string name) =>
        name.Length == 0 ? null : PathGuard.WhyNotPlain(name);
}
