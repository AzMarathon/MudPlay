using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.Services;

namespace FujinTerm.Controls;

// Makes every window OTHER than the main terminal window "leak" its keystrokes
// back to the terminal, so you can keep typing at the game while another window
// is open — unless you're actually editing a text field in that window.
//
// Installed once at startup as a class handler on Window, so it covers every
// window uniformly: the modeless dialogs spawned by DialogService AND the many
// windows shown directly via Window.Show (Character Workshop, Map, Log pane,
// etc.). A per-window attach would miss the latter.
//
// The handlers run on the BUBBLE phase and not for already-handled events, so the
// window's own controls always get first crack: a keystroke only reaches the
// terminal if that window left it unhandled. On top of that we skip forwarding
// when a TextBox owns the focus (you're typing into a field), when the key is one
// the window needs for its own navigation (Tab / Escape / menu chords), and for
// the main window itself (the terminal already handles its own input there).
//
// Forwarded keys run the terminal's real input core (macros, local line buffer,
// command history, escape-sequence mapping) via TerminalInputRouter — identical
// to typing directly in the terminal, not a raw socket write. Gated by
// TerminalInputRouter.Enabled (a user setting); off ⇒ classic focus behaviour.
public static class DialogKeyboardFallthrough
{
    private static bool _installed;
    private static Window? _mainWindow;

    // Register the app-wide class handlers once, capturing the main window so its
    // own input is left untouched. Called from App startup after the main window
    // is created.
    public static void Install(Window mainWindow)
    {
        _mainWindow = mainWindow;
        if (_installed) return;
        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown, RoutingStrategies.Bubble);
        InputElement.TextInputEvent.AddClassHandler<Window>(OnTextInput, RoutingStrategies.Bubble);
    }

    private static void OnKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || ReferenceEquals(window, _mainWindow)) return;
        if (AppServices.CurrentOrNull?.TerminalInput is not { Enabled: true, HasTerminal: true } router) return;
        if (IsTextInputFocused(window)) return;
        if (!ShouldForwardKey(e.Key, e.KeyModifiers)) return;

        // ForwardKey consumes only keys the terminal actually maps (macro,
        // line-edit, escape sequence). A printable/unmapped key returns false and
        // is left unhandled so the follow-up TextInput event carries the character.
        if (router.ForwardKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    private static void OnTextInput(Window window, TextInputEventArgs e)
    {
        if (e.Handled || ReferenceEquals(window, _mainWindow) || string.IsNullOrEmpty(e.Text)) return;
        if (AppServices.CurrentOrNull?.TerminalInput is not { Enabled: true, HasTerminal: true } router) return;
        if (IsTextInputFocused(window)) return;

        if (router.ForwardText(e.Text))
            e.Handled = true;
    }

    // True when the window's focused element is a text editor, so its keystrokes
    // stay with it. A bare TextBox — and the inner editors of NumericUpDown,
    // editable ComboBox and AutoCompleteBox, which all surface a focused TextBox —
    // are covered by the single is-TextBox check (the same test BackscrollWindow
    // uses for its Ctrl+C).
    internal static bool IsTextInputFocused(Visual window)
        => TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement() is TextBox;

    // Keys the window keeps for itself rather than forwarding: focus navigation
    // (Tab), cancel (Escape), the Windows / context-menu keys, and Alt-chord menu
    // accelerators (Alt held without Ctrl). Everything else — typing, arrows,
    // Enter, Backspace, function keys, Ctrl-chords — is eligible to forward.
    internal static bool ShouldForwardKey(Key key, KeyModifiers modifiers)
    {
        if (key is Key.Tab or Key.Escape or Key.LWin or Key.RWin or Key.Apps) return false;
        if ((modifiers & KeyModifiers.Alt) != 0 && (modifiers & KeyModifiers.Control) == 0) return false;
        return true;
    }
}
