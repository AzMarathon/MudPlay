using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The client's own notices are written with dashes and arrows, and reach the
// CP437 grid through Cp437.Encode the way MainWindowViewModel.WriteTerminalStatus
// feeds them. Encoded as Latin-1 they drew "?" for a dash or an arrow and the
// wrong glyph for an accent.
public sealed class TerminalNoticeEncodingTests
{
    // A notice exactly as WriteTerminalStatus frames it: on its own row, in colour.
    private static string Shown(string notice)
    {
        TerminalEmulator emulator = new(80, 24);
        LineExtractor extractor = new(emulator);
        List<string> lines = new();
        extractor.LineEmitted += l => lines.Add(l.Text);
        emulator.Feed(Cp437.Encode($"\r\n\x1b[33;1m{notice}\x1b[0m\r\n"));
        return Assert.Single(lines, l => l.Length > 0);
    }

    [Fact]
    public void DashesArrowsAndEllipses_ReachTheGrid_AsAscii()
    {
        Assert.Equal("[AUTO-RECONNECT ARMED - DIALING AT 07:30:22 (IN 0m03s). PRESS CONNECT TO CANCEL.]",
            Shown("[AUTO-RECONNECT ARMED — DIALING AT 07:30:22 (IN 0m03s). PRESS CONNECT TO CANCEL.]"));
        Assert.Equal("[NO BBS SELECTED - OPEN SETTINGS > BBS + DISPLAY, PICK ONE, AND SAVE.]",
            Shown("[NO BBS SELECTED — OPEN SETTINGS → BBS + DISPLAY, PICK ONE, AND SAVE.]"));
        Assert.Equal("[Walk to Hidden Camp... not started: 'x' 3 x 2]",
            Shown("[Walk to Hidden Camp… not started: ‘x’ 3 × 2]"));
    }

    [Fact]
    public void GlyphsTheGridHas_ArriveAsThemselves()
    {
        // An accent and a box line have CP437 bytes of their own.
        Assert.Equal("[Gnome Ranger équipe ─ ½]", Shown("[Gnome Ranger équipe ─ ½]"));
    }

    [Fact]
    public void AGlyphWithNoStandIn_IsAQuestionMark_NotADroppedOrControlByte()
    {
        // '•' has a CP437 glyph, but only in the low half where the bytes are
        // controls (BEL); it gets a stand-in. A tick mark has none at all.
        Assert.Equal("[* done ?]", Shown("[• done ✓]"));
    }
}
