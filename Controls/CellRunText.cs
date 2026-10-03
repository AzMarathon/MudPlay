using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MudPlay.Controls;

// Draws the text of a run of same-colour terminal cells as ONE glyph run, for the
// live terminal and the history window alike.
//
// Each glyph used to be drawn on its own (a cached FormattedText per cell), so a
// full screen recorded about 2,000 draw calls per frame, and the history window
// even shaped every glyph afresh on each wheel tick. One glyph run per colour run
// brings that down to the number of colour runs.
//
// The glyph run's advances are fixed to the cell width, so every glyph still sits
// exactly on its own cell origin. That is the property the per-glyph drawing
// protected: a run laid out with the font's own advance widths drifts off the cell
// grid by fractions of a pixel and shows as colour bleed between cells, worst on
// the CP437 block glyphs.
//
// No shaping and no font fallback happen here: a character the face has no glyph
// for is drawn on its own as a FormattedText, which falls back to another font the
// way the per-glyph drawing did. Regular weight only — SGR "bold" is BRIGHT and is
// already in the colour the caller passes.
//
// UI-thread only, like the rendering that uses it.
internal sealed class CellRunText
{
    // Glyph index per character; 0 means the face has no glyph for it.
    private readonly Dictionary<char, ushort> _glyphIndex = new();
    private readonly Dictionary<(char Ch, uint Fg), FormattedText> _fallback = new();
    private const int FallbackCap = 4096;

    private Typeface _typeface;
    private GlyphTypeface? _face;
    private double _emSize;
    private double _cellW;
    private double _baseline;
    private ushort _spaceGlyph;

    // Rebuild for a font, size or cell-width change.
    public void Reset(Typeface typeface, double emSize, double cellW)
    {
        _typeface = typeface;
        _emSize = emSize;
        _cellW = cellW;
        _glyphIndex.Clear();
        _fallback.Clear();
        _face = typeface.GlyphTypeface;
        // Where a FormattedText puts the baseline below its top edge: the glyph run is
        // placed on the same line, so the text sits where the per-glyph drawing put it.
        _baseline = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, emSize, Brushes.White).Baseline;
        _spaceGlyph = GlyphFor(' ');
    }

    // Draws chars[0..count) with chars[i] at the cell whose top-left is
    // (left + i * cellW, top). Spaces draw nothing. fgArgb keys the fallback cache.
    public void Draw(DrawingContext context, char[] chars, int count, double left, double top, IBrush fg, uint fgArgb)
    {
        if (count <= 0 || _face is null) return;

        // The run keeps its glyph list and characters past this call (the frame is
        // recorded and drawn later), so both are fresh arrays, never reused buffers.
        GlyphInfo[] glyphs = new GlyphInfo[count];
        bool anyGlyph = false;
        for (int i = 0; i < count; i++)
        {
            char ch = chars[i];
            ushort index = ch == ' ' ? _spaceGlyph : GlyphFor(ch);
            if (index == 0 && ch != ' ')
            {
                context.DrawText(Fallback(ch, fgArgb, fg), new Point(left + i * _cellW, top));
                index = _spaceGlyph;
            }
            else if (ch != ' ')
            {
                anyGlyph = true;
            }
            glyphs[i] = new GlyphInfo(index, i, _cellW);
        }
        if (!anyGlyph) return;

        GlyphRun run = new(_face, _emSize, new ReadOnlyMemory<char>(chars, 0, count), glyphs,
            new Point(left, top + _baseline));
        context.DrawGlyphRun(fg, run);
    }

    private ushort GlyphFor(char ch)
    {
        if (_glyphIndex.TryGetValue(ch, out ushort index)) return index;
        if (_face is null || !_face.CharacterToGlyphMap.TryGetGlyph(ch, out index)) index = 0;
        _glyphIndex[ch] = index;
        return index;
    }

    private FormattedText Fallback(char ch, uint fgArgb, IBrush fg)
    {
        if (_fallback.TryGetValue((ch, fgArgb), out FormattedText? cached)) return cached;
        if (_fallback.Count >= FallbackCap) _fallback.Clear();
        FormattedText ft = new(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _emSize, fg);
        _fallback[(ch, fgArgb)] = ft;
        return ft;
    }
}
