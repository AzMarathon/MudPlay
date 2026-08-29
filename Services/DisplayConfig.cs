using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.Services;

// Live, observable channel for the terminal's display state. ScrollbackLines,
// TerminalCols and TerminalRows mirror the BBS-tier settings; FontFamily,
// FontSize and ScaleToWindow mirror the char-tier General settings. The
// settings sections write into this for live effect; the main window subscribes
// to PropertyChanged and re-applies side effects: font rebind on FontFamily /
// FontSize, scrollback ring resize on ScrollbackLines, emulator screen resize +
// Telnet NAWS re-advertise on TerminalCols / TerminalRows, terminal re-fit on
// ScaleToWindow. AppServices re-resolves these from the active profile / BBS on
// ProfileLoaded / ProfileMutated.
public sealed partial class DisplayConfig : ObservableObject
{
    // The bundled MX437 CP437 bitmap font the TerminalControl renders by
    // default — kept in sync with TerminalControl.FontFamilyProperty's default
    // and used as the fallback whenever the char-tier font choice is unset.
    public const string DefaultFontFamily =
        "avares://MudPlay/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16";

    public const double DefaultFontSize = 16.0;

    // The size the Navigation map hover-tooltip has always rendered at (the
    // FontSize="13" the tooltip's XAML hard-coded before it became configurable).
    public const double DefaultNavTooltipFontSize = 13.0;

    [ObservableProperty] private double _fontSize = DefaultFontSize;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;

    // Rows the Backscroll window advances per mouse-wheel notch. Mirrors the
    // BBS-tier BbsProfile.BackscrollWheelLines; the Backscroll window reads it
    // live so a change applies without reopening. Default 5.
    [ObservableProperty] private int _backscrollWheelLines = 5;

    // Terminal canvas font family, as an avares:// URI. Sourced from the
    // char-tier GeneralSettings.TerminalFontFamily; MainWindowViewModel wraps it
    // into a FontFamily the TerminalControl binds to.
    [ObservableProperty] private string _fontFamily = DefaultFontFamily;

    // Auto-fit the terminal font to the window (keeping the fixed cell grid).
    // Sourced from the char-tier GeneralSettings.ScaleTerminalToWindow.
    [ObservableProperty] private bool _scaleToWindow;

    // Whether the startup attract splash animates. Sourced from the char-tier
    // GeneralSettings.ShowStartupMudAnimation; MainWindowViewModel forwards it to
    // TerminalControl.SplashAnimate so a Settings change stops/starts the running
    // splash live (unchecking + Apply now takes effect immediately instead of only
    // at the next launch). Default true.
    [ObservableProperty] private bool _splashAnimate = true;

    // Navigation map hover-tooltip font, sourced from the char-tier
    // GeneralSettings.NavTooltip* deltas. The Navigation window reads these live
    // when it populates a room tooltip, so a Settings change takes effect on the
    // next hover without reopening the window. Default to the same MX437 face +
    // size 13 the tooltip has always used.
    [ObservableProperty] private string _navTooltipFontFamily = DefaultFontFamily;
    [ObservableProperty] private double _navTooltipFontSize = DefaultNavTooltipFontSize;
}
