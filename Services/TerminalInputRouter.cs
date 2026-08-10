using Avalonia.Input;

namespace MudPlay.Services;

// Bridges keyboard input from any window back to the main terminal. The terminal
// control registers its input core once (RegisterTerminal); other windows'
// keyboard-fallthrough handler (DialogKeyboardFallthrough) calls ForwardKey /
// ForwardText so a keystroke typed while a modeless dialog is focused still lands
// in the terminal — running the SAME macro / line-buffer / history / escape-map
// path as typing directly in the terminal, not a raw socket write.
//
// Enabled is the master gate (a user setting). HasTerminal is false until the
// control registers (and after it detaches), so a forward before the terminal
// exists is a no-op rather than a crash.
public sealed class TerminalInputRouter
{
    // The terminal's input core: (key, modifiers) -> was-consumed, and
    // (text) -> was-consumed. Null when no terminal is registered.
    private System.Func<Key, KeyModifiers, bool>? _onKey;
    private System.Func<string, bool>? _onText;

    // Master enable, mirroring the user setting. When off, ForwardKey / ForwardText
    // are no-ops so focus behaves the classic way (keys stay with the focused window).
    public bool Enabled { get; set; } = true;

    // True once the terminal control has registered its input core.
    public bool HasTerminal => _onKey is not null;

    // Called by TerminalControl when it attaches to the visual tree. A later
    // registration replaces the earlier one (the control is a singleton in
    // practice, but re-attach must not leave a stale delegate).
    public void RegisterTerminal(System.Func<Key, KeyModifiers, bool> onKey, System.Func<string, bool> onText)
    {
        _onKey = onKey;
        _onText = onText;
    }

    // Drop the registration when the terminal detaches, so a forward can't fire
    // into a torn-down control.
    public void UnregisterTerminal()
    {
        _onKey = null;
        _onText = null;
    }

    // Route a non-text key press to the terminal's input core. Returns true when
    // the terminal consumed it (macro, line-edit, or a mapped escape sequence);
    // false when the key isn't one the terminal handles (a printable character —
    // the caller then leaves the KeyDown unhandled so TextInput carries it).
    public bool ForwardKey(Key key, KeyModifiers modifiers)
        => Enabled && _onKey is { } handler && handler(key, modifiers);

    // Route typed text to the terminal's input core (the printable-character path).
    public bool ForwardText(string text)
        => Enabled && _onText is { } handler && handler(text);
}
