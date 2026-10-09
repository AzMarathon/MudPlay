namespace MudPlay.Models.Settings;

// Which colours the 16 base ANSI colours are drawn in (Global tier: one look for
// every character), chosen in Settings → General. Scheme names a
// Terminal.AnsiColorScheme; null or an unknown name is the standard colours. Colors
// is the user's own set, kept for the Custom scheme and across a switch away from
// it, keyed by ANSI index (0–15), each "#RRGGBB"; a colour left at the standard one
// isn't stored.
public sealed class TerminalColorSettings
{
    public string? Scheme { get; set; }
    public Dictionary<int, string>? Colors { get; set; }

    // Nothing worth a place in the file: the standard scheme and no colours kept.
    public bool IsEmpty => string.IsNullOrEmpty(Scheme) && Colors is not { Count: > 0 };
}
