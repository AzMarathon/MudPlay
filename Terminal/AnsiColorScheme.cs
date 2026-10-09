namespace MudPlay.Terminal;

// How the 16 base ANSI colours are drawn (Settings → General).
public enum AnsiColorScheme
{
    // The colours as the client has always drawn them.
    Standard,
    // Schemes that keep the 16 apart for one kind of colour blindness.
    Deuteranopia,
    Protanopia,
    Tritanopia,
    // The user's own colours.
    Custom,
}
