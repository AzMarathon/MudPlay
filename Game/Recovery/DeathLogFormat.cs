using System.Globalization;
using System.Text;
using MudPlay.Models.Profile;
using MudPlay.Terminal;

namespace MudPlay.Game.Recovery;

// The "How did I Die?" death-log file: a short header (who / when / where / lives /
// death line), a dashed rule, then the backscroll tail oldest → newest, one row per
// line as "HH:mm:ss " (blank for a row with no write time) and the row itself. A row
// captured with its cells is written with its colours as ANSI escapes (AnsiEncoder),
// so the file still reads in colour through `less -R` and the viewer can redraw it;
// logs from before colours were kept hold plain text and parse the same way.
public static class DeathLogFormat
{
    private const int TimeWidth = 8;   // "HH:mm:ss"
    private static readonly string Rule = new('-', 60);

    public static string Render(DeathRecord record, IReadOnlyList<TranscriptSnapshot.Line> lines, string characterName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(lines);
        StringBuilder sb = new();
        sb.Append("Death log — ").Append(characterName).Append('\n');
        sb.Append("Died: ").Append(record.DiedText).Append('\n');
        sb.Append("Room: ")
          .Append(record.RoomName ?? "(unknown)").Append("  ").Append(record.RoomKeyText).Append('\n');
        sb.Append("Lives remaining: ").Append(record.LivesRemaining).Append('\n');
        if (!string.IsNullOrWhiteSpace(record.MessageText))
            sb.Append("Death line: ").Append(record.MessageText).Append('\n');
        sb.Append('\n')
          .Append("Last ").Append(lines.Count)
          .Append(" line(s) of backscroll before death (each content row prefixed with its write time):\n");
        sb.Append(Rule).Append('\n');
        foreach (TranscriptSnapshot.Line line in lines)
        {
            sb.Append(line.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : new string(' ', TimeWidth))
              .Append(' ')
              .Append(line.Cells is { } cells ? AnsiEncoder.EncodeRow(cells) : line.Text)
              .Append('\n');
        }
        return sb.ToString();
    }

    // A parsed death log: the header text and the transcript rows, ready for
    // BackscrollView. A row's time of day lands on the death's date (local); a row
    // with no time gets default, which the view leaves unstamped.
    public sealed record Parsed(string Header, IReadOnlyList<ScrollbackBuffer.Row> Rows);

    public static Parsed Parse(string text, DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int rule = Array.IndexOf(lines, Rule);
        if (rule < 0) return new Parsed(text.TrimEnd(), Array.Empty<ScrollbackBuffer.Row>());

        string header = string.Join('\n', lines, 0, rule).TrimEnd();
        DateTime day = diedAt.ToLocalTime().Date;
        List<ScrollbackBuffer.Row> rows = new();
        int last = lines.Length;
        while (last > rule + 1 && lines[last - 1].Length == 0) last--;   // the file's final newline
        for (int i = rule + 1; i < last; i++)
        {
            string line = lines[i];
            DateTimeOffset stamp = default;
            string body = line;
            if (line.Length > TimeWidth && line[TimeWidth] == ' ')
            {
                if (TimeSpan.TryParseExact(line[..TimeWidth], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out TimeSpan tod))
                    stamp = new DateTimeOffset(day + tod);
                body = line[(TimeWidth + 1)..];
            }
            else if (line.Length == TimeWidth && line.Trim().Length == 0)
            {
                body = string.Empty;
            }
            rows.Add(new ScrollbackBuffer.Row(stamp, AnsiEncoder.DecodeRow(body)));
        }
        return new Parsed(header, rows);
    }
}
