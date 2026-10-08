using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.Profile;

// The review shown before a MegaMUD character file becomes a MudPlay character: the
// name it will get, where it will be added, every setting that comes across under
// its MudPlay name, and every one that doesn't with the reason. A setting that comes
// across can be changed here first. Nothing is written until Import is pressed.
public sealed partial class MegaMudImportDialogViewModel : ObservableObject, IDialogViewModel<MegaMudImportChoice>
{
    public event Action<MegaMudImportChoice?>? CloseRequested;

    private readonly Func<string, bool> _exists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorMessage), nameof(HasError), nameof(CanImport))]
    private string _name;

    // Whether the file's BBS user ID and password are stored as this character's
    // login. Only offered when the file has one.
    [ObservableProperty] private bool _importLogin;

    public bool HasLogin { get; }

    // Whether the file's redial and cleanup settings are written onto the BBS.
    // They are the board's own, so this changes them for every character on it:
    // it starts ticked only for a board with no characters yet.
    [ObservableProperty] private bool _applyBbsSettings;
    public bool HasBbsSettings { get; }
    public string BbsSettingsText { get; }
    public string TargetText { get; }
    public string SourceText { get; }
    public IReadOnlyList<MegaMudImportRowViewModel> Imported { get; }
    public IReadOnlyList<MegaMudImportLine> LeftBehind { get; }
    public string ImportedHeader => $"Coming across ({Imported.Count})";
    public string LeftBehindHeader => $"Not coming across ({LeftBehind.Count})";

    public MegaMudImportDialogViewModel(MegaMudImportPlan plan, string fileName, string bbs, string? realm,
        Func<string, bool> exists, bool bbsHasCharacters = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HasBbsSettings = plan.HasBbsSettings;
        _applyBbsSettings = plan.HasBbsSettings && !bbsHasCharacters;
        BbsSettingsText = $"Also set {bbs}'s redial and cleanup settings from the file (they apply to every character on {bbs})";
        _exists = exists ?? throw new ArgumentNullException(nameof(exists));
        _name = plan.SuggestedName;
        HasLogin = plan.HasLogin;
        _importLogin = plan.HasLogin;
        TargetText = string.IsNullOrEmpty(realm) ? $"Added to {bbs}" : $"Added to {bbs}, realm {realm}";
        SourceText = string.IsNullOrEmpty(plan.MegaMudBbsName)
            ? fileName
            : $"{fileName} (MegaMUD board: {plan.MegaMudBbsName})";
        Imported = plan.Lines.Where(static l => l.WasImported).Select(static l => new MegaMudImportRowViewModel(l)).ToList();
        LeftBehind = plan.Lines.Where(static l => !l.WasImported).ToList();
    }

    private string Trimmed => (Name ?? string.Empty).Trim();

    public string ErrorMessage
    {
        get
        {
            string name = Trimmed;
            if (name.Length == 0) return "Give the character a name.";
            foreach (char c in Path.GetInvalidFileNameChars())
                if (name.Contains(c)) return $"The name can't contain '{c}'.";
            if (name is "." or "..") return "That name is reserved by the filesystem.";
            return _exists(name) ? "A character with that name already exists on this BBS." : string.Empty;
        }
    }

    public bool HasError => ErrorMessage.Length > 0;
    public bool CanImport => !HasError;

    [RelayCommand]
    private void Import()
    {
        if (!CanImport) return;
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MegaMudImportRowViewModel row in Imported)
            if (row.IsEdited) edits[row.EditKey] = row.CurrentValue;
        CloseRequested?.Invoke(new MegaMudImportChoice(Trimmed, ImportLogin && HasLogin, ApplyBbsSettings && HasBbsSettings, edits));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
