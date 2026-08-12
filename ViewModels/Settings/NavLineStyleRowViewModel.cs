using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;

namespace MudPlay.ViewModels.Settings;

// One editable row in the Settings → General navigation-line appearance list — a
// live Colour the ColorPicker edits directly plus a thickness stepper, for one map
// polyline (go-to, loop, preview, loop-builder, auto-lair). Modelled on the Talk
// tab's ChannelColorRowViewModel: a field still equal to its NavLineDefaults value
// persists as "no override" (null) so the map falls back to the factory pen and a
// later default tweak still reaches anyone who never customised. Reset snaps both
// fields back to the factory default. Thickness is decimal to bind the Avalonia
// NumericUpDown directly; the double model boundary converts at Load / ToOverride.
public sealed partial class NavLineStyleRowViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public NavLineKind Kind { get; }
    public string Label { get; }
    public Color DefaultColor { get; }
    public double DefaultThickness { get; }

    [ObservableProperty] private Color _color;
    [ObservableProperty] private decimal _thickness;

    // Live preview brush for the swatch button that opens the picker flyout.
    public IBrush Swatch => new SolidColorBrush(Color);

    // Stepper bounds surfaced to the XAML NumericUpDown.
    public decimal MinThickness => (decimal)NavLineDefaults.MinThickness;
    public decimal MaxThickness => (decimal)NavLineDefaults.MaxThickness;

    public NavLineStyleRowViewModel(NavLineKind kind, Action onChanged)
    {
        Kind = kind;
        _onChanged = onChanged;
        (string hex, double thick, string label) = NavLineDefaults.For(kind);
        Label = label;
        DefaultColor = Color.Parse(hex);
        DefaultThickness = thick;
        _color = DefaultColor;
        _thickness = (decimal)thick;
    }

    // "#RRGGBB" / thickness only when they differ from the factory default — a
    // default-equal field writes no delta.
    public string? ColorOverride => Color == DefaultColor ? null : Hex(Color);
    public double? ThicknessOverride => (double)Thickness == DefaultThickness ? null : (double)Thickness;

    // Seed from a persisted delta, falling back to defaults. Caller wraps this in
    // its suppress-dirty window.
    public void Load(NavLineStyle? style)
    {
        Color = Parse(style?.Color, DefaultColor);
        Thickness = (decimal)Clamp(style?.Thickness ?? DefaultThickness);
    }

    // The delta to persist for this line, or null when it's fully default.
    public NavLineStyle? ToOverride()
        => ColorOverride is null && ThicknessOverride is null
            ? null
            : new NavLineStyle { Color = ColorOverride, Thickness = ThicknessOverride };

    [RelayCommand]
    private void Reset()
    {
        Color = DefaultColor;
        Thickness = (decimal)DefaultThickness;
    }

    partial void OnColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Swatch));
        _onChanged();
    }

    partial void OnThicknessChanged(decimal value) => _onChanged();

    // Drop alpha — overrides persist as RGB only, matching what the map renders.
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return Color.Parse(hex); }
        catch { return fallback; }
    }

    private static double Clamp(double t) => Math.Clamp(t, NavLineDefaults.MinThickness, NavLineDefaults.MaxThickness);
}
