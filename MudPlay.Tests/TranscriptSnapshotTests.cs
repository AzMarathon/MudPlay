using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// TranscriptSnapshot.Tail — the shared backscroll-tail capture used by both the
// bug-report scrollback section and the death-log ("How did I Die?") capture.
// Verifies scrollback → live-screen ordering, timestamp presence, trailing-blank
// trim, and the maxLines cap.
public sealed class TranscriptSnapshotTests
{
    private static void WriteRow(TerminalScreen screen, int y, string text)
    {
        for (int x = 0; x < text.Length && x < screen.Cols; x++)
            screen.Put(x, y, new Cell(text[x], default));
    }

    [Fact]
    public void Tail_ScrollbackThenLiveScreen_OldestToNewest_WithTimestampsOnScrollbackOnly()
    {
        TerminalEmulator emulator = new(80, 5);
        TerminalScreen screen = emulator.Screen;

        // Two content rows scroll off into scrollback (timestamped); trailing
        // blank rows are dropped by the clear-capture.
        WriteRow(screen, 0, "SCROLL 1");
        WriteRow(screen, 1, "SCROLL 2");
        screen.ClearAll(default);

        // Two rows on the live screen — the current grid, no per-row timestamp.
        WriteRow(screen, 0, "LIVE A");
        WriteRow(screen, 1, "LIVE B");

        IReadOnlyList<TranscriptSnapshot.Line> lines = TranscriptSnapshot.Tail(emulator, 0);

        Assert.Equal(4, lines.Count);
        Assert.Equal("SCROLL 1", lines[0].Text);
        Assert.NotNull(lines[0].Timestamp);
        Assert.Equal("SCROLL 2", lines[1].Text);
        Assert.NotNull(lines[1].Timestamp);
        Assert.Equal("LIVE A", lines[2].Text);
        Assert.Null(lines[2].Timestamp);
        Assert.Equal("LIVE B", lines[3].Text);
        Assert.Null(lines[3].Timestamp);
    }

    [Fact]
    public void Tail_CapsToLastMaxLines()
    {
        TerminalEmulator emulator = new(80, 3);
        TerminalScreen screen = emulator.Screen;

        WriteRow(screen, 0, "OLD 1");
        WriteRow(screen, 1, "OLD 2");
        screen.ClearAll(default);
        WriteRow(screen, 0, "NEW 1");
        WriteRow(screen, 1, "NEW 2");

        // 2 scrollback + 2 live = 4 total; cap to the newest 2.
        IReadOnlyList<TranscriptSnapshot.Line> lines = TranscriptSnapshot.Tail(emulator, 2);

        Assert.Equal(2, lines.Count);
        Assert.Equal("NEW 1", lines[0].Text);
        Assert.Equal("NEW 2", lines[1].Text);
    }

    [Fact]
    public void RowText_TrimsTrailingSpaces()
    {
        Cell[] cells = "hi   ".Select(c => new Cell(c, default)).ToArray();
        Assert.Equal("hi", TranscriptSnapshot.RowText(cells));
    }
}
