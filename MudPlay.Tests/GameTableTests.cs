using System.Globalization;
using System.Text;
using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A GameTable must give back everything the table's JSON held, exactly as the parsed
// document did: every field in file order, with its kind, its text and what each
// typed read makes of it. The parsed document is the reference throughout.
public sealed class GameTableTests
{
    // Every read a reader of game data makes of one value, as text.
    private static string Reads(JsonElement v)
    {
        StringBuilder sb = new();
        sb.Append(v.ValueKind).Append('|').Append(v.ToString());
        if (v.ValueKind == JsonValueKind.Number)
        {
            sb.Append('|').Append(v.GetRawText());
            sb.Append('|').Append(v.TryGetInt32(out int i)).Append(i);
            sb.Append('|').Append(v.TryGetInt64(out long l)).Append(l);
            sb.Append('|').Append(v.TryGetDouble(out double d)).Append(d.ToString("R", CultureInfo.InvariantCulture));
        }
        else if (v.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            sb.Append('|').Append(v.GetString() ?? "<null>");
        else
            sb.Append('|').Append(v.GetRawText());
        return sb.ToString();
    }

    private static string Reads(GameValue v)
    {
        StringBuilder sb = new();
        sb.Append(v.ValueKind).Append('|').Append(v.ToString());
        if (v.ValueKind == JsonValueKind.Number)
        {
            sb.Append('|').Append(v.GetRawText());
            sb.Append('|').Append(v.TryGetInt32(out int i)).Append(i);
            sb.Append('|').Append(v.TryGetInt64(out long l)).Append(l);
            sb.Append('|').Append(v.TryGetDouble(out double d)).Append(d.ToString("R", CultureInfo.InvariantCulture));
        }
        else if (v.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            sb.Append('|').Append(v.GetString() ?? "<null>");
        else
            sb.Append('|').Append(v.GetRawText());
        return sb.ToString();
    }

    // Row by row and field by field against the parsed document.
    private static void AssertSameAsDocument(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        GameTable table = TestRows.Table(json);

        Assert.Equal(doc.RootElement.GetArrayLength(), table.Rows.Count);
        int r = 0;
        foreach (JsonElement expectedRow in doc.RootElement.EnumerateArray())
        {
            GameRow row = table.Rows[r++];
            if (expectedRow.ValueKind != JsonValueKind.Object)
            {
                Assert.Empty(row.Fields);
                continue;
            }
            List<string> expected = expectedRow.EnumerateObject().Select(p => p.Name + "=" + Reads(p.Value)).ToList();
            List<string> actual = row.Fields.Select(f => f.Name + "=" + Reads(f.Value)).ToList();
            Assert.Equal(expected, actual);

            foreach (JsonProperty property in expectedRow.EnumerateObject())
            {
                // By name too: a name written twice answers with its last value.
                Assert.True(expectedRow.TryGetProperty(property.Name, out JsonElement byName));
                Assert.True(row.TryGetProperty(property.Name, out GameValue value));
                Assert.Equal(Reads(byName), Reads(value));
            }
        }
    }

    [Fact]
    public void Awkward_values_read_back_as_the_document_gave_them()
    {
        AssertSameAsDocument("""
            [
              { "Number": 1, "Name": "plain", "Big": 2147483647, "Bigger": 2147483648, "Huge": 9223372036854775808,
                "Negative": -17, "MinInt": -2147483648, "Decimal": 7.8, "Trailing": 7.0, "Exponent": 1e3, "Signed": 1E+3,
                "NegativeZero": -0, "Zero": 0, "Tiny": 0.000001, "Long": 123456789012 },
              { "Number": 2, "Name": "", "Null": null, "Yes": true, "No": false,
                "Accent": "café – naïve", "Astral": "😀 and 😀", "Nul": "\u0000", "Escapes": "a\"b\\c\/d\n\r\t\u001b",
                "Spaces": "  two  spaces  " },
              { "Number": 3, "Name": "nested", "Object": { "a": [1, 2, { "b": null }], "c": "d" }, "Array": [ 1, "two", [3] ],
                "EmptyObject": {}, "EmptyArray": [] }
            ]
            """);
    }

    [Fact]
    public void Rows_keep_their_own_fields_in_their_own_order()
    {
        AssertSameAsDocument("""
            [
              { "Number": 1, "Name": "a", "Extra": 5 },
              { "Name": "b", "Number": 2 },
              { "Number": 3 },
              {},
              { "Number": 4, "Name": "d", "Extra": 6, "More": "x" },
              { "Number": 5, "Name": "e", "Extra": 7 },
              { "Twice": 1, "Other": 2, "Twice": 3 }
            ]
            """);

        GameTable table = TestRows.Table("""[ { "A": 1, "B": 2 }, { "B": 3 } ]""");
        Assert.False(table.Rows[1].TryGetProperty("A", out _));        // absent, not zero
        Assert.False(table.Rows[0].TryGetProperty("a", out _));        // names match exactly
        Assert.Equal(new[] { "B" }, table.Rows[1].Fields.Select(f => f.Name));
    }

    [Theory]
    [InlineData("""[ 1, "two", null, true, [ { "Number": 9 } ], { "Number": 7 } ]""")]   // elements that are not rows
    [InlineData("[]")]
    [InlineData("""{ "Number": 1 }""")]                                                  // top level is not an array
    [InlineData("7")]
    public void Anything_that_is_not_a_row_reads_as_no_fields(string json)
    {
        GameTable table = TestRows.Table(json);
        using JsonDocument doc = JsonDocument.Parse(json);

        int expectedRows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        Assert.Equal(expectedRows, table.Rows.Count);
        foreach (GameRow row in table.Rows)
            if (!row.TryGetProperty("Number", out GameValue number)) Assert.Empty(row.Fields);
            else Assert.Equal(7, number.GetInt32());
        Assert.Null(table.FindByNumber(9));
    }

    [Theory]
    [InlineData("[{\"Name\":\"Go\u001eblin\"}]")]   // a raw control byte in a string
    [InlineData("[{\"Number\":1}")]                 // cut short
    [InlineData("[{\"Number\":1}] x")]              // something after the table
    [InlineData("[{\"Number\":01}]")]
    [InlineData("")]
    public void Malformed_json_is_refused(string json)
    {
        Assert.ThrowsAny<JsonException>(() => TestRows.Table(json));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json).Dispose());
    }

    // The reader works a block of the file at a time: rows, texts and numbers that
    // straddle a block, and one text longer than a block.
    [Fact]
    public void A_table_larger_than_the_readers_block_reads_whole()
    {
        StringBuilder json = new("[");
        for (int i = 0; i < 6000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"Number\":{i},\"Name\":\"monster {i % 97}\",\"Note\":\"{new string((char)('a' + i % 26), i % 211)}\",\"Value\":{i * 1.5}}}");
        }
        json.Append(",{\"Number\":999999,\"Name\":\"long\",\"Note\":\"").Append('x', 300_000).Append("\"}]");

        AssertSameAsDocument(json.ToString());
    }

    [Fact]
    public void A_wrong_kind_read_throws_as_the_document_did()
    {
        GameRow row = TestRows.Row("""{ "Text": "12", "Whole": 12, "Fraction": 1.5, "Null": null, "Flag": true }""");
        GameValue Field(string name) => row.TryGetProperty(name, out GameValue v) ? v : throw new KeyNotFoundException(name);

        Assert.Throws<InvalidOperationException>(() => Field("Text").TryGetInt32(out _));
        Assert.Throws<InvalidOperationException>(() => Field("Text").GetDouble());
        Assert.Throws<InvalidOperationException>(() => Field("Null").TryGetInt64(out _));
        Assert.Throws<InvalidOperationException>(() => Field("Whole").GetString());
        Assert.Throws<InvalidOperationException>(() => Field("Whole").GetBoolean());
        Assert.Throws<FormatException>(() => Field("Fraction").GetInt32());
        Assert.Throws<FormatException>(() => Field("Fraction").GetInt64());
        Assert.False(Field("Fraction").TryGetInt32(out int left));
        Assert.Equal(0, left);                       // not the digits it got through
        Assert.Equal(1.5, Field("Fraction").GetDouble());
        Assert.True(Field("Flag").GetBoolean());
        Assert.Null(Field("Null").GetString());

        // A value no row produced, and a row no table produced.
        Assert.Equal(JsonValueKind.Undefined, default(GameValue).ValueKind);
        Assert.Throws<InvalidOperationException>(() => default(GameValue).GetString());
        Assert.False(default(GameRow).TryGetProperty("Number", out _));
        Assert.Empty(default(GameRow).Fields);
    }

    [Fact]
    public void Number_and_name_lookups_take_the_first_row_of_a_tie()
    {
        GameTable table = TestRows.Table("""
            [
              { "Number": 5, "Name": "Orc", "Which": "first" },
              { "Number": 5, "Name": "orc", "Which": "second" },
              { "Number": "6", "Name": 6, "Which": "not a number, not a name" },
              { "Number": 7.0, "Name": null, "Which": "not whole as written" },
              { "Number": -0, "Name": "zero", "Which": "minus zero is zero" }
            ]
            """);
        static string Which(GameRow? row) =>
            row!.Value.TryGetProperty("Which", out GameValue v) ? v.GetString()! : "<none>";

        Assert.Equal("first", Which(table.FindByNumber(5)));
        Assert.Equal("first", Which(table.FindByName("ORC")));
        Assert.Equal("minus zero is zero", Which(table.FindByNumber(0)));
        Assert.Null(table.FindByNumber(6));
        Assert.Null(table.FindByNumber(7));
        Assert.Null(table.FindByName("6"));
        Assert.Equal(new[] { 0, 5 }, table.Numbers.Order());
    }

    // Background builders read a table while the UI thread does: every read, and the
    // first use of each index, from many threads at once.
    [Fact]
    public void Reads_from_many_threads_agree_with_one()
    {
        StringBuilder json = new("[");
        for (int i = 1; i <= 3000; i++)
        {
            if (i > 1) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"Number\":{i},\"Name\":\"thing {i}\",\"Exit\":\"{i % 13}/{i % 7}\",\"Weight\":{i % 5 + 0.5}}}");
        }
        json.Append(']');
        string text = json.ToString();

        string[] expected = TestRows.Table(text).Rows.Select(Line).ToArray();
        GameTable shared = TestRows.Table(text);

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, worker =>
        {
            for (int i = 1; i <= 3000; i++)
            {
                int n = (i * 31 + worker) % 3000 + 1;
                Assert.Equal(expected[n - 1], Line(shared.FindByNumber(n)!.Value));
                Assert.Equal(expected[n - 1], Line(shared.FindByName("THING " + n)!.Value));
                Assert.Equal(expected[n - 1], Line(shared.Rows[n - 1]));
            }
        });

        static string Line(GameRow row) => string.Join(";", row.Fields.Select(f => f.Name + "=" + Reads(f.Value)));
    }

    [Fact]
    public void A_table_holds_a_fraction_of_its_file()
    {
        // 2,000 rows of mostly small numbers and repeated text, as the imported tables are.
        StringBuilder json = new("[");
        for (int i = 0; i < 2000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture, $"{{\"Number\":{i},\"Name\":\"room\",\"N\":\"0\",\"S\":\"{i % 50}/1\"");
            for (int a = 0; a < 20; a++) json.Append(CultureInfo.InvariantCulture, $",\"Abil-{a}\":0");
            json.Append('}');
        }
        json.Append(']');

        GameTable table = TestRows.Table(json.ToString());

        Assert.InRange(table.ResidentBytes, 1, json.Length / 2);
    }
}
