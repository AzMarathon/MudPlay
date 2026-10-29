using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace MudPlay.Services;

// Every window opens on the client's screen: over the window it was opened from
// (the main window, unless another window owns it), never wherever the window
// manager likes. CenterOwner alone isn't enough: KDE under Wayland (the client
// runs through XWayland) ignores the position a window asks for before it is
// mapped and drops new windows on the first monitor, a second monitor's client
// having to drag every one across. A position set once the window is open is
// honoured, so the placement is made then.
//
// Installed once at startup as a class handler, so it covers the DialogService
// dialogs and the windows shown directly alike. Windows with a remembered layout
// are placed by WindowLayoutStore instead (onto the main window's screen too),
// and are skipped here.
public static class WindowPlacement
{
    private static bool _installed;
    private static Window? _mainWindow;
    private static Func<Window, bool>? _placedElsewhere;

    // placedElsewhere: true for a window another placement owns (a remembered layout).
    public static void Install(Window mainWindow, Func<Window, bool> placedElsewhere)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(placedElsewhere);
        _mainWindow = mainWindow;
        _placedElsewhere = placedElsewhere;
        if (_installed) return;
        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnOpened);
    }

    private static void OnOpened(Window window, RoutedEventArgs e)
    {
        if (_mainWindow is null || ReferenceEquals(window, _mainWindow)) return;
        if (_placedElsewhere?.Invoke(window) == true) return;
        if (CenteredOnAnchor(window) is { } at) window.Position = at;
    }

    // Where window opens: centred over its owner (else the main window), kept
    // inside that window's screen. Null when there is nothing to anchor to.
    public static PixelPoint? CenteredOnAnchor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Window? anchor = window.Owner as Window ?? _mainWindow;
        if (anchor is null || ReferenceEquals(anchor, window) || window.WindowState != WindowState.Normal) return null;
        if (window.Screens.ScreenFromWindow(anchor) is not { } screen) return null;
        PixelRect over = new(anchor.Position, FramePixels(anchor, screen.Scaling));
        return CenteredOver(over, FramePixels(window, screen.Scaling), screen.WorkingArea);
    }

    // The screen the main window is on, or null (no main window yet, or none found).
    public static Screen? MainScreen(Window window) =>
        _mainWindow is { } main && !ReferenceEquals(main, window) ? window.Screens.ScreenFromWindow(main) : null;

    // A window's outer frame in physical pixels, the unit positions and screens use.
    public static PixelSize FramePixels(Window window, double scaling)
    {
        Size size = window.FrameSize ?? window.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            size = new Size(double.IsNaN(window.Width) ? 0 : window.Width, double.IsNaN(window.Height) ? 0 : window.Height);
        return new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
    }

    // Where a frame of this size sits centred over the anchor, kept inside area.
    public static PixelPoint CenteredOver(PixelRect anchor, PixelSize frame, PixelRect area) =>
        ClampInto(new PixelPoint(anchor.X + (anchor.Width - frame.Width) / 2, anchor.Y + (anchor.Height - frame.Height) / 2),
            frame, area);

    // A window remembered on another screen, moved to the same spot on the target
    // screen (its offset from that screen's corner kept), and kept inside its
    // working area.
    public static PixelPoint OntoScreen(PixelRect saved, PixelRect fromScreen, PixelRect toScreen, PixelRect toArea) =>
        ClampInto(new PixelPoint(toScreen.X + saved.X - fromScreen.X, toScreen.Y + saved.Y - fromScreen.Y),
            saved.Size, toArea);

    // The point nearest start that keeps the frame inside area; a frame larger
    // than the area keeps its top-left corner on it.
    public static PixelPoint ClampInto(PixelPoint start, PixelSize frame, PixelRect area)
    {
        int maxX = Math.Max(area.X, area.Right - frame.Width);
        int maxY = Math.Max(area.Y, area.Bottom - frame.Height);
        return new PixelPoint(Math.Clamp(start.X, area.X, maxX), Math.Clamp(start.Y, area.Y, maxY));
    }
}
