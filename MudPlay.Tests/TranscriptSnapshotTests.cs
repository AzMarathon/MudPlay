using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// TranscriptSnapshot.Tail — the shared backscroll-tail capture used by both the
// bug-report scrollback section and the death-log ("How did I Die?") capture.
// Verifies scrollback → live-screen ordering, per-row write timestamps (now on
// live rows too, not just scrolled-off ones), trailing-blank trim, and the
// maxLines cap — plus the TerminalScreen row-stamp lockstep across a scroll.
public sealed class TranscriptSnapshotTests
{
    // Mirror what TerminalEmulator.Feed does — stamp the batch, then write cells.
    private static void WriteRow(TerminalScreen screen, int y, string text, DateTimeOffset? stamp = null)
    {
        if (stamp is { } s) screen.FeedTimestamp = s;
        for (int x = 0; x < text.Length && x < screen.Cols; x++)
            screen.Put(x, y, new Cell(text[x], default));
    }

    [Fact]
    public void Tail_LiveAndScrollbackRows_BothCarryWriteTimestamps()
    {
        TerminalEmulator emulator = new(80, 5);
        TerminalScreen screen = emulator.Screen;

        // Two content rows scroll off into scrollback (stamped at capture);
        // trailing blank rows are dropped by the clear-capture.
        WriteRow(screen, 0, "SCROLL 1");
        WriteRow(screen, 1, "SCROLL 2");
        screen.ClearAll(default);

        // Two rows still on the live screen, written by a Feed batch — each now
        // carries that batch's write time (previously blank in the snapshot).
        DateTimeOffset liveStamp = new(2026, 8, 11, 20, 31, 3, TimeSpan.Zero);
        WriteRow(screen, 0, "LIVE A", liveStamp);
        WriteRow(screen, 1, "LIVE B", liveStamp);

        IReadOnlyList<TranscriptSnapshot.Line> lines = TranscriptSnapshot.Tail(emulator, 0);

        Assert.Equal(4, lines.Count);
        Assert.Equal("SCROLL 1", lines[0].Text);
        Assert.NotNull(lines[0].Timestamp);
        Assert.Equal("SCROLL 2", lines[1].Text);
        Assert.NotNull(lines[1].Timestamp);
        Assert.Equal("LIVE A", lines[2].Text);
        Assert.Equal(liveStamp, lines[2].Timestamp);
        Assert.Equal("LIVE B", lines[3].Text);
        Assert.Equal(liveStamp, lines[3].Timestamp);
    }

    [Fact]
    public void Tail_BlankInteriorLiveRow_HasNoTimestamp()
    {
        TerminalEmulator emulator = new(80, 4);
        TerminalScreen screen = emulator.Screen;
        DateTimeOffset stamp = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);
        WriteRow(screen, 0, "TOP", stamp);
        // row 1 left blank (interior spacing the user saw)
        WriteRow(screen, 2, "BOTTOM", stamp);

        IReadOnlyList<TranscriptSnapshot.Line> lines = TranscriptSnapshot.Tail(emulator, 0);

        // TOP, (blank), BOTTOM — the blank interior row keeps a null timestamp so
        // the snapshot never stamps empty spacing rows.
        Assert.Equal(3, lines.Count);
        Assert.Equal("TOP", lines[0].Text);
        Assert.NotNull(lines[0].Timestamp);
        Assert.Equal("", lines[1].Text);
        Assert.Null(lines[1].Timestamp);
        Assert.Equal("BOTTOM", lines[2].Text);
        Assert.NotNull(lines[2].Timestamp);
    }

    [Fact]
    public void Feed_StampsLiveRows_MonotonicAcrossBatches()
    {
        // The real path: content arrives through Feed, which stamps each batch.
        TerminalEmulator emulator = new(80, 5);
        emulator.Feed("ALPHA\r\n"u8);
        emulator.Feed("BRAVO\r\n"u8);
        emulator.Feed("CHARLIE"u8);   // no trailing LF — cursor rests on this row

        IReadOnlyList<TranscriptSnapshot.Line> lines = TranscriptSnapshot.Tail(emulator, 0);
        TranscriptSnapshot.Line alpha = lines.Single(l => l.Text == "ALPHA");
        TranscriptSnapshot.Line bravo = lines.Single(l => l.Text == "BRAVO");
        TranscriptSnapshot.Line charlie = lines.Single(l => l.Text == "CHARLIE");

        Assert.NotNull(alpha.Timestamp);
        Assert.NotNull(bravo.Timestamp);
        Assert.NotNull(charlie.Timestamp);
        // Separate Feed batches → non-decreasing write times, oldest line first.
        Assert.True(alpha.Timestamp <= bravo.Timestamp);
        Assert.True(bravo.Timestamp <= charlie.Timestamp);
    }

    [Fact]
    public void ScrollUp_ShiftsRowStampsWithRows_AndResetsRevealedRow()
    {
        // The riskiest part of the feature: the per-row stamp array must move in
        // lockstep with the cells so a scrolled screen doesn't mislabel rows.
        TerminalScreen screen = new(10, 3);
        DateTimeOffset t0 = new(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = new(2026, 8, 11, 1, 0, 1, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 8, 11, 1, 0, 2, TimeSpan.Zero);
        WriteRow(screen, 0, "A", t0);
        WriteRow(screen, 1, "B", t1);
        WriteRow(screen, 2, "C", t2);

        screen.ScrollUp(0, 2, 1, default);   // A scrolls off; B→row0, C→row1; row2 revealed blank

        Assert.Equal(t1, screen.RowTimestamp(0));   // B's stamp rode up with it
        Assert.Equal(t2, screen.RowTimestamp(1));   // C's stamp rode up with it
        Assert.Null(screen.RowTimestamp(2));        // revealed row reset to "never written"
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
