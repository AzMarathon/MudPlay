using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MudPlay.Controls;

// Gives every window other than the main one the same title shape:
// "<its own title> — <profile> — <bbs>:<realm>", the tail the main window already
// carries. With several clients open, a Settings or Navigation window otherwise
// says nothing about which character it belongs to.
//
// Installed once at startup as class handlers on Window, like
// DialogKeyboardFallthrough, so it covers the dialogs DialogService spawns, the
// floating panels and the windows shown directly, with no per-window wiring.
//
// A window's own title may be fixed in XAML or bound to its view-model. The tail is
// written with SetCurrentValue, which leaves a binding in place; when the binding
// later pushes a new title, that becomes the window's own title and is re-tailed.
public static class WindowTitleTail
{
    private const string Separator = " — ";

    private static bool _installed;
    private static Window? _mainWindow;
    private static Func<string>? _tail;
    private static string _lastTail = string.Empty;
    private static bool _stamping;
    // Each open window's own title, without the tail.
    private static readonly Dictionary<Window, string> OwnTitles = new();

    // tail: the profile and BBS part, read whenever a title is written.
    public static void Install(Window mainWindow, Func<string> tail)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(tail);
        _mainWindow = mainWindow;
        _tail = tail;
        _lastTail = tail();
        if (_installed) return;
        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnOpened);
        Window.WindowClosedEvent.AddClassHandler<Window>(OnClosed);
        Window.TitleProperty.Changed.AddClassHandler<Window>(OnTitleChanged);
    }

    // The profile or the BBS may have changed: re-tail every open window. Cheap to
    // call often — nothing is written unless the tail really differs.
    public static void Refresh()
    {
        string tail = _tail?.Invoke() ?? string.Empty;
        if (tail == _lastTail) return;
        _lastTail = tail;
        foreach ((Window window, string own) in OwnTitles.ToList()) Stamp(window, own);
    }

    // The full title for a window whose own title is ownTitle.
    internal static string Compose(string? ownTitle, string tail)
    {
        string own = ownTitle?.Trim() ?? string.Empty;
        if (tail.Length == 0) return own;
        return own.Length == 0 ? tail : own + Separator + tail;
    }

    private static void OnOpened(Window window, RoutedEventArgs e)
    {
        // A window hidden and shown again opens twice; its own title is already known.
        if (ReferenceEquals(window, _mainWindow) || OwnTitles.ContainsKey(window)) return;
        string own = window.Title ?? string.Empty;
        OwnTitles[window] = own;
        Stamp(window, own);
    }

    private static void OnClosed(Window window, RoutedEventArgs e) => OwnTitles.Remove(window);

    private static void OnTitleChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        if (_stamping || !OwnTitles.ContainsKey(window)) return;
        string own = e.NewValue as string ?? string.Empty;
        OwnTitles[window] = own;
        Stamp(window, own);
    }

    private static void Stamp(Window window, string own)
    {
        _stamping = true;
        try { window.SetCurrentValue(Window.TitleProperty, Compose(own, _lastTail)); }
        finally { _stamping = false; }
    }
}
