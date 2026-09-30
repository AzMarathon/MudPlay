using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// DeathLogFormat — the format of a captured
// "How did I Die?" log: a short header (who / when / where / lives / death line)
// followed by the backscroll tail, oldest → newest.
public sealed class DeathLogRenderTests
{
    [Fact]
    public void RenderDeathLog_IncludesHeaderAndTranscript()
    {
        var record = new DeathRecord(
            DateTimeOffset.UnixEpoch, new RoomRef(3, 42), 5,
            "You have been killed!")
        {
            RecordNumber = 1,
            RoomName = "A dark alley",
        };

        var lines = new List<TranscriptSnapshot.Line>
        {
            new(DateTimeOffset.UnixEpoch, "the orc swings a rusty axe"),
            new(null, "You have been killed!"),
        };

        string text = DeathLogFormat.Render(record, lines, "Grushnak");

        Assert.Contains("Death log — Grushnak", text);
        Assert.Contains("A dark alley", text);
        Assert.Contains("3/42", text);
        Assert.Contains("Lives remaining: 5", text);
        Assert.Contains("Death line: You have been killed!", text);
        Assert.Contains("the orc swings a rusty axe", text);
    }

    [Fact]
    public void RenderDeathLog_OmitsDeathLine_WhenMessageBlank()
    {
        var record = new DeathRecord(DateTimeOffset.UnixEpoch, null, 0, null)
        {
            RecordNumber = 2,
        };

        var lines = new List<TranscriptSnapshot.Line> { new(null, "some final line") };

        string text = DeathLogFormat.Render(record, lines, "Nobody");

        Assert.DoesNotContain("Death line:", text);
        Assert.Contains("some final line", text);
    }

    private static readonly CellAttributes Red = new(TerminalColor.Indexed(9), TerminalColor.Default, CellFlags.Bold);

    private static Cell[] Cells(string text, CellAttributes attr)
    {
        Cell[] cells = new Cell[text.Length];
        for (int i = 0; i < text.Length; i++) cells[i] = new Cell(text[i], attr);
        return cells;
    }

    // A row captured with its cells keeps its colours through the file and back.
    [Fact]
    public void ColouredRows_RoundTripThroughTheFile()
    {
        var record = new DeathRecord(new DateTimeOffset(2026, 9, 30, 8, 27, 40, TimeSpan.Zero), null, 3, null)
        {
            RecordNumber = 4,
        };
        Cell[] hit = Cells("The orc hits you for 9 damage!", Red);
        var lines = new List<TranscriptSnapshot.Line>
        {
            new(new DateTimeOffset(2026, 9, 30, 8, 27, 36, TimeSpan.Zero), "The orc hits you for 9 damage!", hit),
            new(null, "", Array.Empty<Cell>()),
        };

        string text = DeathLogFormat.Render(record, lines, "Grushnak");
        Assert.Contains("\x1b[", text);   // stored as ANSI

        DeathLogFormat.Parsed parsed = DeathLogFormat.Parse(text, record.At);
        Assert.Contains("Death log — Grushnak", parsed.Header);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(hit, parsed.Rows[0].Cells);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 27, 36, TimeSpan.Zero).ToLocalTime().TimeOfDay,
            parsed.Rows[0].Timestamp.TimeOfDay);
        Assert.Empty(parsed.Rows[1].Cells);
        Assert.Equal(default, parsed.Rows[1].Timestamp);
    }

    // A log written before colours were kept reads as plain default-colour rows.
    [Fact]
    public void PlainLogs_StillParse()
    {
        var record = new DeathRecord(DateTimeOffset.UnixEpoch, null, 0, null) { RecordNumber = 5 };
        string text = DeathLogFormat.Render(record,
            new List<TranscriptSnapshot.Line> { new(DateTimeOffset.UnixEpoch, "plain line") }, "Old");

        DeathLogFormat.Parsed parsed = DeathLogFormat.Parse(text, record.At);
        ScrollbackBuffer.Row row = Assert.Single(parsed.Rows);
        Assert.Equal("plain line", TranscriptSnapshot.RowText(row.Cells));
        Assert.All(row.Cells, c => Assert.Equal(CellAttributes.Default, c.Attr));
    }

    [Fact]
    public void AnsiEncoder_DecodesWhatItEncodes()
    {
        var bg = new CellAttributes(TerminalColor.Rgb(10, 20, 30), TerminalColor.Indexed(4), CellFlags.Underline | CellFlags.Reverse);
        Cell[] row = Cells("ab", Red).Concat(Cells("cd", bg)).Concat(Cells("e", CellAttributes.Default)).ToArray();
        Assert.Equal(row, AnsiEncoder.DecodeRow(AnsiEncoder.EncodeRow(row)));
    }
}
