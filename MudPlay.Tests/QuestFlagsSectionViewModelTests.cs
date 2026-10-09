using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Quests;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// Pins the Quest Flags table's rows and its filter box: the requirement columns reach the
// grid, a flag is found by its number without dragging in every other number that contains
// those digits, and by its name.
public sealed class QuestFlagsSectionViewModelTests : IDisposable
{
    private readonly string _root;

    public QuestFlagsSectionViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questflags-section-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Flags 13, 130 and 133, with "13" also turning up as a give value and in a room number.
    private const string TBInfoJson = """
        [
          { "Number": 1, "Action": "touch stone:minlevel 12:class 2:checkitem 7:takeitem 8:giveability 13 1\n", "Called From": "Room 1/130" },
          { "Number": 2, "Action": "giveability 130 13\n", "Called From": "Room 1/5" },
          { "Number": 3, "Action": "giveability 133 1\n", "Called From": "" }
        ]
        """;

    private async Task<QuestFlagsSectionViewModel> LoadAsync()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), """[ { "Number": 2, "Name": "Mage" } ]""");
        File.WriteAllText(Path.Combine(dir, "Items.json"),
            """[ { "Number": 7, "Name": "rune" }, { "Number": 8, "Name": "candle" } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        QuestFlagsSectionViewModel vm = new(new QuestFlagIndex(cache), cache);
        await vm.LoadAsync();
        return vm;
    }

    private static void Filter(QuestFlagsSectionViewModel vm, string text)
    {
        vm.SearchText = text;
        vm.ApplySearchCommand.Execute(null);   // the box searches on Enter
    }

    [Fact]
    public async Task Rows_CarryTheLinesCommandAndRequirements()
    {
        QuestFlagsSectionViewModel vm = await LoadAsync();

        GameDataRow row = vm.AllRows.Single(r => r.Get("Flag") == "13");
        Assert.Equal("touch stone", row.Get("Command"));
        Assert.Equal("12+", row.Get("Level"));
        Assert.Equal("Mage", row.Get("Class"));
        Assert.Equal("rune, candle (taken)", row.Get("Items"));
        Assert.Equal("1/130", row.Get("Location"));
    }

    [Fact]
    public async Task Filter_FlagNumber_ShowsThatFlagOnly()
    {
        QuestFlagsSectionViewModel vm = await LoadAsync();

        Filter(vm, "13");

        // Not flag 130 or 133, nor the row whose give value is 13.
        Assert.Equal(new[] { "13" }, vm.FilteredRows.Select(r => r.Get("Flag")));
    }

    [Fact]
    public async Task Filter_FlagName_FindsTheFlag()
    {
        QuestFlagsSectionViewModel vm = await LoadAsync();

        Filter(vm, "phoenix");

        Assert.Equal(new[] { "133" }, vm.FilteredRows.Select(r => r.Get("Flag")));
    }

    [Fact]
    public async Task Filter_OtherText_StillMatchesAnyColumn()
    {
        QuestFlagsSectionViewModel vm = await LoadAsync();

        Filter(vm, "1/5");

        Assert.Equal(new[] { "130" }, vm.FilteredRows.Select(r => r.Get("Flag")));
    }
}
