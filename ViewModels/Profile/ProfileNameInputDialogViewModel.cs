using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.Profile;

// Name-a-profile dialog (Profile Management's add / rename / copy, and saving the
// unnamed profile). The name becomes the character's folder under BBS/{bbs}/profiles/.
// Inline validates the name against filesystem-unsafe characters and surfaces an
// overwrite warning when that name already exists; Save commits on Enter / button,
// Cancel / X returns null.
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

    // A line above the box saying why a name is being asked for, when the user
    // didn't ask to name anything (a plain Save on the default profile).
    public string? Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    // A third way out, "Discard", for when the name is asked for because what the
    // unnamed draft holds is about to be lost. The dialog still returns no name;
    // Discarded tells that apart from backing out with Cancel.
    public bool OfferDiscard { get; }

    public bool Discarded { get; private set; }

    public ProfileNameInputDialogViewModel(string suggestedName, Func<string, bool> exists, string? note = null,
        bool offerDiscard = false)
    {
        _exists = exists;
        Name    = suggestedName;
        Note    = note;
        OfferDiscard = offerDiscard;
    }

    [RelayCommand]
    private void Discard()
    {
        Discarded = true;
        CloseRequested?.Invoke(null);
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
