using Avalonia.Media;

namespace MudPlay.Services;

// Enumerates the OS-installed font families for the font pickers, in two lists
// built by one scan:
//   Monospace — the fixed-pitch ones, for the terminal (and anything drawn on its
//     cell grid). A proportional font would mangle the fixed CP437 grid, so
//     anything that isn't monospace is dropped rather than left for the user to
//     pick and regret.
//   Text — every family that can render ordinary text, fixed-pitch or not, for the
//     Conversation window, whose rows are plain wrapped text with no grid. Emoji
//     and symbol-only faces are dropped: they carry no Latin letters.
//
// Built once, then cached for the app's lifetime — probing every installed
// family is too costly to repeat each time Settings opens. Detection reads glyph
// advances straight off the font's metric table (no FormattedText layout pass),
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

    // Family names of every installed font that renders text, monospace included,
    // sorted and de-duplicated the same way.
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

    private static (IReadOnlyList<string> Monospace, IReadOnlyList<string> Text) Build()
    {
        SortedSet<string> mono = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> text = new(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontManager.Current.SystemFonts)
        {
            if (string.IsNullOrWhiteSpace(family.Name)) continue;
            if (TryGlyph(family) is not { } glyph) continue;
            if (!RendersText(glyph)) continue;
            text.Add(family.Name);
            // Probe the actual Latin advances rather than trusting the font's own
            // IsFixedPitch flag — that flag is set on emoji/symbol faces (all-uniform
            // but no text glyphs) and on some proportional Nerd Font variants, none of
            // which render BBS text at fixed pitch. Requiring 'i'/'M'/'W'/space to be
            // present and equal keeps only faces that behave as a monospace font.
            if (AdvancesUniform(glyph)) mono.Add(family.Name);
        }
        return (mono.ToArray(), text.ToArray());
    }

    private static GlyphTypeface? TryGlyph(FontFamily family)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? glyph)
                ? glyph : null;
        }
        catch
        {
            // A family that can't be realised into a glyph typeface (a broken or
            // partial install) simply doesn't make the cut — skip it rather than
            // let one bad font abort the whole enumeration.
            return null;
        }
    }

    // A face that can set ordinary text: it carries the Latin letters a chat line is
    // made of. Emoji / symbol / dingbat faces don't, and would render boxes.
    private static bool RendersText(GlyphTypeface glyph) =>
        Advance(glyph, 'a') > 0 && Advance(glyph, 'M') > 0 && Advance(glyph, ' ') > 0;

    private static bool AdvancesUniform(GlyphTypeface glyph)
    {
        double em = glyph.Metrics.DesignEmHeight;
        if (em <= 0) return false;

        // Advances come back in font design units, independent of point size, so
        // the ratio between them is all that matters. A narrow 'i', a wide
        // 'M'/'W', and a space all advancing the same width is the fixed-pitch
        // signature. Any missing probe glyph (advance <= 0) fails it.
        double[] advances =
        {
            Advance(glyph, 'i'),
            Advance(glyph, 'M'),
            Advance(glyph, 'W'),
            Advance(glyph, ' '),
        };
        return WidthsUniform(advances, epsilon: em * 0.02);
    }

    private static double Advance(GlyphTypeface glyph, char ch) =>
        glyph.CharacterToGlyphMap.TryGetGlyph(ch, out ushort g) && g != 0
            && glyph.TryGetHorizontalGlyphAdvance(g, out ushort adv)
                ? adv
                : -1;

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
