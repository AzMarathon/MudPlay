using System.Text;

namespace MudPlay.Terminal;

// Re-emits a Cell[] row as plain text plus inline ANSI SGR escapes, so a
// captured log file rendered through less -R, a modern terminal, or any
// ANSI-aware viewer shows the same colours the user saw live. Mirrors the
// run-batching Controls.CellRowDisplay does for on-screen rendering — same
// logical groupings, different output medium.
public static class AnsiEncoder
{
    // Encode cells as ANSI text. Always ends with ESC [ 0 m so the output
    // doesn't bleed colour into whatever follows it in the same file / pipe.
    public static string EncodeRow(ReadOnlySpan<Cell> cells)
    {
        StringBuilder sb = new(cells.Length + 32);
        if (cells.Length == 0) return string.Empty;

        CellAttributes? lastAttr = null;

        for (int i = 0; i < cells.Length; i++)
        {
            CellAttributes attr = cells[i].Attr;
            if (lastAttr is null || !attr.Equals(lastAttr.Value))
            {
                EmitSgr(sb, attr);
                lastAttr = attr;
            }
            sb.Append(cells[i].Char);
        }

        sb.Append("\x1b[0m");
        return sb.ToString();
    }

    // The inverse of EncodeRow: read a row this encoder wrote back into cells, so a saved
    // capture can be drawn in colour again. Text with no escapes (a capture from before
    // colours were kept) decodes to plain default-colour cells. Only SGR ("m")
    // sequences carry meaning here; any other escape sequence is skipped whole.
    public static Cell[] DecodeRow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Cell> cells = new(text.Length);
        CellAttributes attr = CellAttributes.Default;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c != '\x1b') { cells.Add(new Cell(c, attr)); i++; continue; }
            if (i + 1 >= text.Length || text[i + 1] != '[') { i++; continue; }
            int end = i + 2;
            while (end < text.Length && (text[end] is (>= '0' and <= '9') or ';')) end++;
            if (end >= text.Length) break;
            if (text[end] == 'm') attr = ApplySgr(attr, text.Substring(i + 2, end - i - 2));
            i = end + 1;
        }
        return cells.ToArray();
    }

    private static CellAttributes ApplySgr(CellAttributes attr, string parameters)
    {
        string[] p = parameters.Length == 0 ? new[] { "0" } : parameters.Split(';');
        for (int k = 0; k < p.Length; k++)
        {
            int code = int.TryParse(p[k], out int n) ? n : 0;
            switch (code)
            {
                case 0: attr = CellAttributes.Default; break;
                case 1: attr = attr with { Flags = attr.Flags | CellFlags.Bold }; break;
                case 4: attr = attr with { Flags = attr.Flags | CellFlags.Underline }; break;
                case 7: attr = attr with { Flags = attr.Flags | CellFlags.Reverse }; break;
                case 8: attr = attr with { Flags = attr.Flags | CellFlags.Concealed }; break;
                case 38 or 48:
                    TerminalColor color;
                    if (k + 2 < p.Length && p[k + 1] == "5")
                    {
                        color = TerminalColor.Indexed(int.TryParse(p[k + 2], out int idx) ? idx : 0);
                        k += 2;
                    }
                    else if (k + 4 < p.Length && p[k + 1] == "2")
                    {
                        color = TerminalColor.Rgb(Byte(p[k + 2]), Byte(p[k + 3]), Byte(p[k + 4]));
                        k += 4;
                    }
                    else break;
                    attr = code == 38 ? attr with { Foreground = color } : attr with { Background = color };
                    break;
            }
        }
        return attr;

        static byte Byte(string s) => byte.TryParse(s, out byte b) ? b : (byte)0;
    }

    private static void EmitSgr(StringBuilder sb, CellAttributes attr)
    {
        sb.Append("\x1b[0");   // reset before re-applying the run's attrs

        if ((attr.Flags & CellFlags.Bold)      != 0) sb.Append(";1");
        if ((attr.Flags & CellFlags.Underline) != 0) sb.Append(";4");
        if ((attr.Flags & CellFlags.Reverse)   != 0) sb.Append(";7");
        if ((attr.Flags & CellFlags.Concealed) != 0) sb.Append(";8");

        AppendColor(sb, attr.Foreground, isBackground: false);
        AppendColor(sb, attr.Background, isBackground: true);

        sb.Append('m');
    }

    private static void AppendColor(StringBuilder sb, TerminalColor color, bool isBackground)
    {
        switch (color.Kind)
        {
            case ColorKind.Indexed:
                sb.Append(isBackground ? ";48;5;" : ";38;5;").Append(color.Value);
                break;
            case ColorKind.Rgb:
                uint rgb = color.Value;
                byte r = (byte)((rgb >> 16) & 0xFF);
                byte g = (byte)((rgb >> 8)  & 0xFF);
                byte b = (byte)( rgb        & 0xFF);
                sb.Append(isBackground ? ";48;2;" : ";38;2;")
                  .Append(r).Append(';').Append(g).Append(';').Append(b);
                break;
            // Default: emit nothing — the reset already restored default.
        }
    }
}
