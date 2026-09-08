using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.Services;

// Live, observable channel for the app's display state. ScrollbackLines,
// TerminalCols and TerminalRows mirror the BBS-tier settings; FontFamily,
// FontSize and ScaleToWindow mirror the char-tier General settings; the
// NavTooltip* and Convo* fonts mirror char-tier General / Talk settings for the
// Navigation and Conversation windows. The settings sections write into this for
// live effect; the subscribers re-apply side effects: the main window rebinds the
// terminal font on FontFamily / FontSize, resizes the scrollback ring on
// ScrollbackLines, resizes the emulator screen + re-advertises Telnet NAWS on
// TerminalCols / TerminalRows, and re-fits on ScaleToWindow; the Navigation window
// reads NavTooltip* on the next hover; the Conversation window subscribes and
// re-fonts its rows live on Convo* changes. AppServices re-resolves all of these
// from the active profile / BBS on ProfileLoaded / ProfileMutated.
public sealed partial class DisplayConfig : ObservableObject
{
    // The bundled MX437 CP437 bitmap font the TerminalControl renders by
    // default — kept in sync with TerminalControl.FontFamilyProperty's default
    // and used as the fallback whenever the char-tier font choice is unset.
    public const string DefaultFontFamily =
        "avares://MudPlay/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16";

    public const double DefaultFontSize = 12.0;

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

    // Conversation window row font, sourced from the char-tier TalkSettings.Convo*
    // deltas. Unlike the terminal / nav-tooltip fonts these hold the RAW delta —
    // an empty family and a 0 size mean "use the built-in default", resolved by the
    // Conversation VM (empty -> bundled JetBrains Mono, 0 -> 12pt). The window
    // observes these so a Settings -> Talk Apply re-fonts the open window on the
    // spot instead of only on the next open.
    [ObservableProperty] private string _convoFontFamily = "";
    [ObservableProperty] private double _convoFontSize;
}
