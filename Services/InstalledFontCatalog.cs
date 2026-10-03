using Avalonia.Media;
using SkiaSharp;

namespace MudPlay.Services;

// Enumerates the OS-installed font families for the font pickers, in two lists
// built by one scan:
//   Monospace — the fixed-pitch ones, for the terminal (and anything drawn on its
//     cell grid). A proportional font would mangle the fixed CP437 grid, so
//     anything that isn't monospace is dropped rather than left for the user to
//     pick and regret.
//   Text — every installed family, for the Conversation window. Its rows are
//     plain wrapped text with no grid and no ANSI art to keep aligned, so nothing
//     is ruled out; a letter the chosen face lacks falls back to another font.
//
// Built once, then cached for the app's lifetime — probing every installed
// family is too costly to repeat each time Settings opens. Detection reads glyph
// advances straight from Skia (no glyph typeface, no FormattedText layout pass),
// which is both far cheaper and safe to run off the UI thread, so Warm() can
// pre-build the list on a background thread at startup and the first Settings
// open pays nothing.
public static class InstalledFontCatalog
{
    private static readonly object _gate = new();
    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text)? _lists;

    // Family names of every installed monospace font, sorted case-insensitively
    // and de-duplicated across weights.
    public static IReadOnlyList<string> Monospace => Lists.Monospace;

    // Family names of every installed font, monospace included, sorted and
    // de-duplicated the same way.
    public static IReadOnlyList<string> Text => Lists.Text;

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Lists
    {
        get
        {
            if (_lists is { } cached) return cached;
            lock (_gate)
                return _lists ??= Build();
        }
    }

    // Pre-build the catalogue off the UI thread so opening Settings doesn't stall
    // on the first enumeration. Safe to call before any window exists — it only
    // touches the font subsystem. Failures are swallowed: the lazy getter simply
    // rebuilds on demand if the warm-up couldn't complete.
    public static void Warm()
    {
        try { _ = Lists; }
        catch { /* best-effort pre-warm; the picker rebuilds lazily if this fails */ }
    }

    // The probe letters, in the order IsFixedPitch reads them.
    private const string ProbeText = "aiMW ";

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Build()
    {
        SortedSet<string> mono = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> text = new(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontManager.Current.SystemFonts)
        {
            if (string.IsNullOrWhiteSpace(family.Name)) continue;
            (bool usable, bool fixedPitch) = ProbeFamily(family.Name);
            if (usable) text.Add(family.Name);
            if (fixedPitch) mono.Add(family.Name);
        }
        return (mono.ToArray(), text.ToArray());
    }

    // Deliberately not through FontManager.TryGetGlyphTypeface: for a family whose
    // name carries a weight ("Roboto Black") that call synthesises a bold face by
    // copying the whole font file out of a native stream the garbage collector can
    // free mid-copy, which kills the process outright in optimised builds. This
    // scan runs for every installed family at launch, while startup is allocating
    // hardest, so it asks Skia for the face and its advances directly.
    private static (bool Usable, bool FixedPitch) ProbeFamily(string family)
    {
        try
        {
            using SKTypeface? face = SKFontManager.Default.MatchFamily(family);
            return face is null ? default : (true, IsFixedPitch(face));
        }
        catch
        {
            // A family that can't be realised (a broken or partial install) simply
            // doesn't make the cut — skip it rather than let one bad font abort the
            // whole enumeration.
            return default;
        }
    }

    // Whether a face sets BBS text at fixed pitch, which is what the terminal's
    // cell grid needs.
    internal static bool IsFixedPitch(SKTypeface face)
    {
        int em = face.UnitsPerEm;
        if (em <= 0) return false;

        // Sized to the em with hinting off, so the advances come back in font
        // design units and only the ratio between them matters.
        using SKFont font = new(face, em)
        {
            LinearMetrics = true,
            Hinting = SKFontHinting.None,
            Subpixel = true,
        };
        Span<float> advances = stackalloc float[ProbeText.Length];
        font.GetGlyphWidths(ProbeText, advances, Span<SKRect>.Empty);
        // A missing letter still measures (as the face's "not defined" box), so ask
        // for the glyph itself.
        for (int i = 0; i < ProbeText.Length; i++)
            if (face.GetGlyph(ProbeText[i]) == 0) advances[i] = -1;

        // Probe the actual Latin advances rather than trusting the font's own
        // IsFixedPitch flag — that flag is set on emoji/symbol faces (all-uniform
        // but no text glyphs) and on some proportional Nerd Font variants, none of
        // which render BBS text at fixed pitch. A narrow 'i', a wide 'M' / 'W' and a
        // space all advancing the same width is the fixed-pitch signature; a face
        // missing any of them fails, since WidthsUniform rejects the -1. An 'a' has
        // to be there too, though not at the same width.
        if (advances[0] <= 0) return false;
        double[] pitch = { advances[1], advances[2], advances[3], advances[4] };
        return WidthsUniform(pitch, epsilon: em * 0.02);
    }

    // A fixed-pitch face advances its probe glyphs by the same width. Compare
    // within a tolerance (a small fraction of the em) to absorb the odd
    // design-unit rounding; any zero or negative width fails it outright, since a
    // face that can't measure a probe glyph isn't one to offer.
    internal static bool WidthsUniform(IReadOnlyList<double> widths, double epsilon = 0.5)
    {
        if (widths.Count == 0) return false;
        double first = widths[0];
        if (first <= 0) return false;
        foreach (double w in widths)
        {
            if (w <= 0) return false;
            if (Math.Abs(w - first) > epsilon) return false;
        }
        return true;
    }
}
