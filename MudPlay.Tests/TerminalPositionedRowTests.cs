using System.Text;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// MajorMUD paints a drawn card by clearing the screen and addressing every row
// with CUP — the art and the two rows of text under it. Only the last row ends in
// a line feed, so the row that names the card reached no parser and a wanted card
// was drawn over again (reports paradigm-20261002-140153 and -140334). The bytes
// here are cut from that capture.
public sealed class TerminalPositionedRowTests
{
    private const string Esc = "\u001b";

    private static List<string> Feed(string wire)
    {
        TerminalEmulator emulator = new(80, 24);
        LineExtractor extractor = new(emulator);
        List<string> lines = new();
        extractor.LineEmitted += l => lines.Add(l.Text);
        emulator.Feed(Encoding.Latin1.GetBytes(wire));
        return lines;
    }

    private static string SunCard() =>
        "[HP=137/MA=66]:" + Esc + "[0m" + Esc + "[79D" + Esc + "[K" + Esc + "[0;32m"
        + Esc + "[2J" + Esc + "[B" + Esc + "[0;30m " + Esc + "[0;30;47mÚÄÄ¿" + Esc + "[0;1;30mÜ"
        + Esc + "[3;1H " + Esc + "[0;30;47m³The³"
        + Esc + "[15;1H " + Esc + "[0;32m\"The Sun is a boon to all those who search for things concealed. Keen"
        + Esc + "[16;1H  perception is yours, and nothing shall remain hidden from you for long.\""
        + Esc + "[\r\n"
        + Esc + "[79D" + Esc + "[K" + Esc + "[0;37m[HP=137/MA=66]:";

    [Fact]
    public void TheRowNamingTheCard_IsEmitted_ThoughItNeverGetsALineFeed()
    {
        List<string> lines = Feed(SunCard());

        Assert.Contains(" \"The Sun is a boon to all those who search for things concealed. Keen", lines);
    }

    [Fact]
    public void PositionedRows_AreEmittedInTheOrderTheyWerePainted()
    {
        List<string> lines = Feed(SunCard());

        Assert.Equal(
            new[]
            {
                " ┌──┐▄",
                " │The│",
                " \"The Sun is a boon to all those who search for things concealed. Keen",
                "  perception is yours, and nothing shall remain hidden from you for long.\"",
            },
            lines);
    }

    // The textblock ends on a bare "ESC [" before the line break. The prompt that
    // follows used to be glued onto the text row with "79D" printed in front of it.
    [Fact]
    public void ASequenceCutShortByALineBreak_DoesNotSwallowTheBreakOrTheNextSequence()
    {
        List<string> lines = Feed(SunCard() + "look deck\r\n");

        Assert.DoesNotContain(lines, l => l.Contains("79D"));
        Assert.Equal("  perception is yours, and nothing shall remain hidden from you for long.\"", lines[3]);
        Assert.Equal("[HP=137/MA=66]:", lines[4]);
        Assert.Equal("look deck", lines[5]);
    }

    [Fact]
    public void ARowFinishedByALineFeed_IsNotEmittedAgainWhenTheCursorLaterMovesOffIt()
    {
        List<string> lines = Feed("first\r\nsecond" + Esc + "[A" + Esc + "[5;1Hthird\r\n");

        Assert.Equal(new[] { "first", "second", "third" }, lines);
    }

    [Fact]
    public void ARowThatWasOnlyErased_IsNotEmittedWhenTheCursorLeavesIt()
    {
        List<string> lines = Feed("[HP=10]:" + Esc + "[79D" + Esc + "[K" + Esc + "[5;1Hnext\r\n");

        Assert.Equal(new[] { "next" }, lines);
    }

    // A screen clear takes the unfinished row with it: the prompt that was sitting
    // there is not a line of output.
    [Fact]
    public void ClearingTheScreen_DropsTheUnfinishedRow()
    {
        List<string> lines = Feed("[HP=10]:" + Esc + "[2J" + Esc + "[3;1Hart" + Esc + "[4;1Hmore\r\n");

        Assert.Equal(new[] { "art", "more" }, lines);
    }
}
