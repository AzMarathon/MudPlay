using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Terminal;

namespace MudPlay.ViewModels.Settings;

// One of the 16 base ANSI colours in the Settings → General colour list: what it is
// called, the colour it is drawn in under the scheme picked, and a sample of text
// in it on the terminal's black. Under the Custom scheme the swatch opens a picker
// and Reset puts the standard colour back; under the others the row only shows.
public sealed partial class TerminalColorRowViewModel : ObservableObject
{
    private readonly Action<TerminalColorRowViewModel> _onEdited;

    public int Index { get; }
    public string Label { get; }
    public Color StandardColor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    private Color _color;

    // The row can be edited: the Custom scheme is the one picked.
    [ObservableProperty] private bool _canEdit;

    public IBrush Brush => new SolidColorBrush(Color);

    public TerminalColorRowViewModel(int index, Action<TerminalColorRowViewModel> onEdited)
    {
        Index = index;
        Label = AnsiColorSchemes.NameOf(index);
        StandardColor = ToColor(AnsiPalette.Default16[index]);
        _color = StandardColor;
        _onEdited = onEdited;
    }

    public uint Argb => 0xFF000000u | ((uint)Color.R << 16) | ((uint)Color.G << 8) | Color.B;

    // Show a colour without it counting as the user's edit.
    public void Show(uint argb)
    {
        _showing = true;
        Color = ToColor(argb);
        _showing = false;
    }

    private bool _showing;

    [RelayCommand]
    private void Reset() => Color = StandardColor;

    partial void OnColorChanged(Color value)
    {
        if (!_showing) _onEdited(this);
    }

    private static Color ToColor(uint argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
