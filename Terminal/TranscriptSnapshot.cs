using System.Text;

namespace MudPlay.Terminal;

// Snapshots the tail of the terminal transcript — the rows that have scrolled off
// the top plus the live screen — as timestamped text lines. Shared by the
// bug-report scrollback section and the death-log capture so both read one
// consistent view of "the last N lines the user saw".
public static class TranscriptSnapshot
{
    // One captured transcript line. Timestamp is the wall-clock instant a
    // scrolled-off row was captured; null for live-screen rows, which have no
    // per-row time (they're the current grid, not yet aged into scrollback).
    public readonly record struct Line(DateTimeOffset? Timestamp, string Text);

    // The last maxLines transcript lines, oldest → newest: every scrolled-off
    // scrollback row followed by the current live screen, with trailing blank
    // padding rows trimmed. A non-positive maxLines returns the whole transcript.
    public static IReadOnlyList<Line> Tail(TerminalEmulator emulator, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        List<Line> lines = new();
        foreach (ScrollbackBuffer.Row row in emulator.Screen.Scrollback.Enumerate())
            lines.Add(new Line(row.Timestamp, RowText(row.Cells)));

        TerminalScreen screen = emulator.Screen;
        for (int y = 0; y < screen.Rows; y++)
            lines.Add(new Line(null, RowText(screen.Row(y))));

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
