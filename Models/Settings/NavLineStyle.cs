namespace MudPlay.Models.Settings;

// One nav line's appearance override. Delta-only, mirroring the Talk tab's channel
// colours: a null field means "use the NavLineDefaults value," so the on-disk
// Global settings stay empty until the user actually changes something and a later
// default tweak still reaches anyone who never customised.
public sealed class NavLineStyle
{
    // Override colour as a "#RRGGBB" (or "#AARRGGBB") hex string; null = default.
    public string? Color { get; set; }

    // Override stroke thickness in DIPs; null = default.
    public double? Thickness { get; set; }
}
