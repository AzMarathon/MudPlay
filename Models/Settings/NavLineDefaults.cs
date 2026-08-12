namespace MudPlay.Models.Settings;

// Single source of truth for each nav line's factory appearance — the exact hex +
// thickness MapControl shipped with hardcoded. Both the renderer (falls back here
// when no override is set) and the Settings → General editor (default swatch +
// "Restore Defaults") read from this, so the two can never drift.
public static class NavLineDefaults
{
    // (default colour hex, default stroke thickness, editor label) per line.
    public static (string Hex, double Thickness, string Label) For(NavLineKind kind) => kind switch
    {
        NavLineKind.Goto        => ("#1E64DC", 3.0, "Go-to (walk-to) line"),
        NavLineKind.Loop        => ("#7AB870", 3.0, "Loop line"),
        NavLineKind.Preview     => ("#E66C5A", 3.0, "Preview line"),
        NavLineKind.LoopBuilder => ("#E66C5A", 3.0, "Loop-builder preview line"),
        NavLineKind.AutoLair    => ("#DC821E", 3.0, "Auto-Lair line"),
        _                       => ("#FFFFFF", 3.0, "?"),
    };

    // Editor + render order — matches the top-to-bottom rows in the settings pane.
    public static readonly IReadOnlyList<NavLineKind> All = new[]
    {
        NavLineKind.Goto, NavLineKind.Loop, NavLineKind.Preview,
        NavLineKind.LoopBuilder, NavLineKind.AutoLair,
    };

    // Thickness bounds for the editor stepper (a hairline to a bold highlight).
    public const double MinThickness = 1.0;
    public const double MaxThickness = 8.0;
}
