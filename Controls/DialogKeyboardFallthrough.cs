using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.Services;

namespace FujinTerm.Controls;

// Makes a modeless dialog "leak" its keystrokes back to the main terminal, so you
// can keep typing at the game while another window is open — unless you're
// actually editing a text field in the dialog. Attached once per window by
// DialogService.
//
// The handlers run on the BUBBLE phase (and not for already-handled events), so
// the dialog's own controls always get first crack: a keystroke only reaches the
// terminal if the dialog left it unhandled. On top of that we skip forwarding
// when a TextBox owns the focus (you're typing into a field) and when the key is
// one the dialog needs for its own navigation (Tab / Escape / menu chords).
//
// Forwarded keys run the terminal's real input core (macros, local line buffer,
// command history, escape-sequence mapping) via TerminalInputRouter — identical
// to typing directly in the terminal, not a raw socket write. The whole thing is
// gated by TerminalInputRouter.Enabled (a user setting); off ⇒ classic focus
// behaviour.
public static class DialogKeyboardFallthrough
{
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        window.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not Visual v) return;
        if (AppServices.CurrentOrNull?.TerminalInput is not { Enabled: true, HasTerminal: true } router) return;
        if (IsTextInputFocused(v)) return;
        if (!ShouldForwardKey(e.Key, e.KeyModifiers)) return;

        // ForwardKey consumes only keys the terminal actually maps (macro,
        // line-edit, escape sequence). A printable/unmapped key returns false and
        // is left unhandled so the follow-up TextInput event carries the character.
        if (router.ForwardKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || sender is not Visual v) return;
        if (AppServices.CurrentOrNull?.TerminalInput is not { Enabled: true, HasTerminal: true } router) return;
        if (IsTextInputFocused(v)) return;

        if (router.ForwardText(e.Text))
            e.Handled = true;
    }

    // True when the window's focused element is a text editor, so its keystrokes
    // stay with it. A bare TextBox — and the inner editors of NumericUpDown,
    // editable ComboBox and AutoCompleteBox, which all surface a focused TextBox —
    // are covered by the single is-TextBox check (the same test BackscrollWindow
    // uses for its Ctrl+C).
    internal static bool IsTextInputFocused(Visual anyElementInWindow)
        => TopLevel.GetTopLevel(anyElementInWindow)?.FocusManager?.GetFocusedElement() is TextBox;

    // Keys the dialog keeps for itself rather than forwarding: focus navigation
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
