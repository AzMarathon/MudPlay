using System.Text;

namespace MudPlay.Terminal;

// Snapshots the tail of the terminal transcript — the rows that have scrolled off
// the top plus the live screen — as timestamped text lines. Shared by the
// bug-report scrollback section and the death-log capture so both read one
// consistent view of "the last N lines the user saw".
public static class TranscriptSnapshot
{
    // One captured transcript line. Timestamp is the wall-clock instant the row's
    // content was written — the same per-row write stamp whether the row has since
    // scrolled off into the ring or is still on screen, so the timestamps stay in
    // order across the boundary. Null only for a blank row (no content was ever
    // written, so there is no meaningful time). Cells is the row's coloured cells,
    // trimmed like Text, when the caller asked for them; null otherwise.
    public readonly record struct Line(DateTimeOffset? Timestamp, string Text, Cell[]? Cells = null);

    // The last maxLines transcript lines, oldest → newest: every scrolled-off
    // scrollback row followed by the current live screen, with trailing blank
    // padding rows trimmed. A non-positive maxLines returns the whole transcript.
    // withCells also copies each row's cells, for a capture that keeps its colours
    // (the death log); the text-only callers skip the copy.
    // only, when given, leaves out every row written outside its stretches: the
    // bug report passes the time spent in the game, so a login screen that is in
    // the backscroll, or still on the terminal, isn't copied into a report. A blank
    // on-screen row has no time to judge by and goes the way of the row above it.
    public static IReadOnlyList<Line> Tail(TerminalEmulator emulator, int maxLines, bool withCells = false,
        CaptureWindow? only = null)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        List<Line> lines = new();
        foreach (ScrollbackBuffer.Row row in emulator.Screen.Scrollback.Enumerate())
        {
            if (only is not null && !only.Covers(row.Timestamp)) continue;
            string text = RowText(row.Cells);
            lines.Add(new Line(row.Timestamp, text, withCells ? row.Cells[..text.Length] : null));
        }

        TerminalScreen screen = emulator.Screen;
        bool kept = only is null || lines.Count > 0;
        for (int y = 0; y < screen.Rows; y++)
        {
            ReadOnlySpan<Cell> cells = screen.Row(y);
            string text = RowText(cells);
            // A blank live row has no meaningful write time — keep it null so the
            // snapshot doesn't stamp empty spacing rows. Content rows carry their
            // per-row write stamp.
            DateTimeOffset? written = text.Length == 0 ? null : screen.RowTimestamp(y);
            if (only is not null && written is { } at) kept = only.Covers(at);
            if (!kept) continue;
            lines.Add(new Line(written, text, withCells ? cells[..text.Length].ToArray() : null));
        }

        // Trim only trailing blank padding from the live screen; interior blanks
        // may be meaningful spacing the user actually saw.
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);

        if (maxLines > 0 && lines.Count > maxLines)
            lines.RemoveRange(0, lines.Count - maxLines);

        return lines;
    }

    // Collapse a cell row to its text, dropping trailing spaces so the grid's
    // right-pad doesn't bloat the capture.
    public static string RowText(ReadOnlySpan<Cell> cells)
    {
        StringBuilder sb = new(cells.Length);
        foreach (Cell c in cells) sb.Append(c.Char);
        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ') end--;
        return sb.ToString(0, end);
    }
}
