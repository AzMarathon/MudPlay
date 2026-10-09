using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;
using MudPlay.Terminal;

namespace MudPlay.ViewModels.Settings;

// The terminal-colour picker in Settings → General: one of five schemes for the 16
// base ANSI colours (the standard colours, one made for each of three kinds of
// colour blindness, or the user's own), and the 16 colours of the scheme picked.
// The user's own colours are kept while another scheme is picked, so going back to
// Custom finds them as they were left.
public sealed partial class TerminalColorsEditorViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly uint[] _custom = AnsiPalette.Default16.ToArray();
    private bool _loading;
    // The scheme on show before the one just picked.
    private AnsiColorScheme _shown = AnsiColorScheme.Standard;

    // Normal intensity (0–7) and bright (8–15), each a column of the list.
    public ObservableCollection<TerminalColorRowViewModel> NormalRows { get; } = new();
    public ObservableCollection<TerminalColorRowViewModel> BrightRows { get; } = new();

    [ObservableProperty] private bool _isStandard = true;
    [ObservableProperty] private bool _isDeuteranopia;
    [ObservableProperty] private bool _isProtanopia;
    [ObservableProperty] private bool _isTritanopia;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    private bool _isCustom;

    public string Hint => IsCustom
        ? "Click a colour to change it. Reset puts that one back to the standard colour."
        : "The 16 colours as this choice draws them. Pick Custom to set your own.";

    public TerminalColorsEditorViewModel(Action onChanged)
    {
        _onChanged = onChanged;
        for (int i = 0; i < AnsiPalette.BaseColorCount; i++)
            (i < 8 ? NormalRows : BrightRows).Add(new TerminalColorRowViewModel(i, OnRowEdited));
    }

    public AnsiColorScheme Scheme =>
        IsCustom ? AnsiColorScheme.Custom
        : IsDeuteranopia ? AnsiColorScheme.Deuteranopia
        : IsProtanopia ? AnsiColorScheme.Protanopia
        : IsTritanopia ? AnsiColorScheme.Tritanopia
        : AnsiColorScheme.Standard;

    // Seed from the saved setting. The caller holds its own dirty flag down.
    public void Load(TerminalColorSettings? saved)
    {
        _loading = true;
        AnsiColorSchemes.CustomColors(saved?.Colors).CopyTo(_custom, 0);
        AnsiColorScheme scheme = AnsiColorSchemes.Parse(saved?.Scheme);
        IsStandard     = scheme == AnsiColorScheme.Standard;
        IsDeuteranopia = scheme == AnsiColorScheme.Deuteranopia;
        IsProtanopia   = scheme == AnsiColorScheme.Protanopia;
        IsTritanopia   = scheme == AnsiColorScheme.Tritanopia;
        IsCustom       = scheme == AnsiColorScheme.Custom;
        _loading = false;
        ShowScheme();
    }

    // What to save, or null for the standard scheme with no colours of the user's
    // own to keep.
    public TerminalColorSettings? ToSettings()
    {
        TerminalColorSettings settings = new()
        {
            Scheme = Scheme == AnsiColorScheme.Standard ? null : Scheme.ToString(),
            Colors = AnsiColorSchemes.CustomDelta(_custom),
        };
        return settings.IsEmpty ? null : settings;
    }

    // Put every one of the user's own colours back to the standard one.
    [RelayCommand]
    private void RestoreStandardColors()
    {
        AnsiPalette.Default16.CopyTo(_custom, 0);
        if (IsCustom) ShowScheme();
        _onChanged();
    }

    partial void OnIsStandardChanged(bool value)     => OnSchemePicked(value);
    partial void OnIsDeuteranopiaChanged(bool value) => OnSchemePicked(value);
    partial void OnIsProtanopiaChanged(bool value)   => OnSchemePicked(value);
    partial void OnIsTritanopiaChanged(bool value)   => OnSchemePicked(value);
    partial void OnIsCustomChanged(bool value)       => OnSchemePicked(value);

    // The radio group unchecks the old choice and checks the new one; act on the
    // check only.
    private void OnSchemePicked(bool isChecked)
    {
        if (_loading || !isChecked) return;
        // Custom picked with no colour of the user's own yet: start from the scheme
        // they were looking at, so one of the made schemes can be adjusted instead
        // of rebuilt from the standard colours.
        if (IsCustom && _custom.AsSpan().SequenceEqual(AnsiPalette.Default16))
            AnsiColorSchemes.For(_shown).ToArray().CopyTo(_custom, 0);
        ShowScheme();
        _onChanged();
    }

    private void ShowScheme()
    {
        _shown = Scheme;
        IReadOnlyList<uint> colors = IsCustom ? _custom : AnsiColorSchemes.For(Scheme);
        foreach (TerminalColorRowViewModel row in NormalRows.Concat(BrightRows))
        {
            row.CanEdit = IsCustom;
            row.Show(colors[row.Index]);
        }
    }

    private void OnRowEdited(TerminalColorRowViewModel row)
    {
        if (!IsCustom) return;
        _custom[row.Index] = row.Argb;
        _onChanged();
    }
}
