using System.Text.Json;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// A row keeps its raw values for search and filters and its formatted ones for the
// grid, by column name or by cell position.
public sealed class GameDataRowTests
{
    private static readonly string[] Columns = { "Number", "Name", "Type", "Weight" };

    private static GameDataRow Row(string json, IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null,
        IReadOnlyDictionary<string, string?>? computed = null)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return GameDataRow.FromJson(doc.RootElement, Columns, formatters, computed);
    }

    [Fact]
    public void Raw_and_displayed_values_are_read_by_column_whatever_its_case()
    {
        var formatters = new Dictionary<string, Func<string?, string?>> { ["Type"] = raw => raw == "2" ? "Undead" : raw };
        GameDataRow row = Row("""{"Number":7,"Name":"ghoul","Type":2}""", formatters);

        Assert.Equal("2", row.Get("type"));
        Assert.Equal("Undead", row.GetDisplay("TYPE"));
        Assert.Equal("ghoul", row.GetDisplay("Name"));
        Assert.Null(row.Get("Weight"));          // column missing from the source row
        Assert.Null(row.Get("NoSuchColumn"));
    }

    [Fact]
    public void Cells_carry_the_displayed_values_in_column_order()
    {
        var formatters = new Dictionary<string, Func<string?, string?>> { ["Type"] = _ => "Undead" };
        GameDataRow row = Row("""{"Number":7,"Name":"ghoul","Type":2,"Weight":150}""", formatters);

        Assert.Equal(new[] { "Number", "Name", "Type", "Weight" }, row.Cells.Select(c => c.Column));
        Assert.Equal(new[] { "7", "ghoul", "Undead", "150" }, row.Cells.Select(c => c.Value));
        Assert.Equal(4, row.CellCount);
        Assert.Equal("Undead", row.DisplayAt(2));
        Assert.Same(row.Cells, row.Cells);
    }

    [Fact]
    public void A_computed_cell_replaces_the_source_value()
    {
        GameDataRow row = Row("""{"Number":7,"Name":"ghoul"}""",
            computed: new Dictionary<string, string?> { ["Weight"] = "heavy" });

        Assert.Equal("heavy", row.Get("Weight"));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1023", "1023")]
    [InlineData("1024", "1024")]
    [InlineData("-3", "-3")]
    [InlineData("7.0", "7.0")]
    [InlineData("2.5", "2.5")]
    public void A_number_keeps_the_text_the_data_wrote(string json, string expected)
    {
        GameDataRow row = Row($$"""{"Number":{{json}}}""");

        Assert.Equal(expected, row.Get("Number"));
    }
}
