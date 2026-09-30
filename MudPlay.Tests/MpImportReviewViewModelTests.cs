using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Reshaping the MudPlay side of a .mp import: a step's row left out of the loop, a
// room inserted below a row, rows moved — and that shape surviving a re-translation.
public sealed class MpImportReviewViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-mpreview-tests-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // A square: 1/1 Start —N→ 1/2 North —E→ 1/4 Corner —S→ 1/3 East —W→ 1/1.
    private const string Square = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "N": "1/2", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North",
            "N": "0", "S": "1/1", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "East",
            "N": "1/4", "S": "0", "E": "0", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Corner",
            "N": "0", "S": "1/3", "E": "0", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private MpImportReviewViewModel Review()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Square);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager g = new(cache);
        g.OnActiveSetChanged("alpha");
        string H(int room) => MegaMudHash.ComputeHashExits(g.GetRoom(new RoomKey(1, room))!);
        string[] steps = { $"{H(1)}:0000:n", $"{H(2)}:0000:e", $"{H(4)}:0000:s", $"{H(3)}:0000:w" };
        MpLoopFile file = MpFileParser.Parse(
            $"[Square-1 1][Tester]\n[SQRE:Test:Start]\n{H(1)}:{H(1)}:4:-1:0:::\n" + string.Join("\n", steps));
        return new MpImportReviewViewModel("square.mp", file, null, new MpFileImporter(g), g,
            new LoopManager(new BfsMapper(g), g), filter: null);
    }

    private static RoomKey?[] Rooms(MpImportReviewViewModel vm) =>
        vm.Rows.Select(r => r.ToWaypoint()?.Key).ToArray();

    [Fact]
    public void RemovedStepStaysOnScreenButLeavesTheLoop()
    {
        MpImportReviewViewModel vm = Review();
        MpTranslatedRowViewModel north = vm.Rows[1];

        vm.RemoveRowCommand.Execute(north);

        Assert.Equal(4, vm.Rows.Count);
        Assert.True(north.IsRemoved);
        Assert.Null(north.ToWaypoint());

        vm.RemoveRowCommand.Execute(north);
        Assert.Equal(new RoomKey(1, 2), north.ToWaypoint()?.Key);
    }

    [Fact]
    public void InsertedRowTakesItsTypedRoom_AndMovesAndGoes()
    {
        MpImportReviewViewModel vm = Review();

        vm.InsertBelowCommand.Execute(vm.Rows[0]);
        MpTranslatedRowViewModel added = vm.Rows[1];
        Assert.True(added.IsInserted);
        Assert.Null(added.ToWaypoint());
        added.RoomText = "1/3";
        Assert.Equal(new RoomKey?[] { new(1, 1), new(1, 3), new(1, 2), new(1, 4), new(1, 3) }, Rooms(vm));

        vm.MoveDownCommand.Execute(added);
        Assert.Same(added, vm.Rows[2]);

        vm.RemoveRowCommand.Execute(added);
        Assert.Equal(4, vm.Rows.Count);
        Assert.DoesNotContain(added, vm.Rows);
    }

    [Fact]
    public void ShapeSurvivesARetranslation()
    {
        MpImportReviewViewModel vm = Review();
        vm.RemoveRowCommand.Execute(vm.Rows[2]);
        vm.InsertBelowCommand.Execute(vm.Rows[0]);
        vm.Rows[1].RoomText = "1/3";
        // A room typed on a step re-translates every step's row.
        vm.Rows[0].RoomText = "1/1";
        vm.VerifyCommand.Execute(null);

        Assert.Equal(5, vm.Rows.Count);
        Assert.True(vm.Rows[1].IsInserted);
        Assert.True(vm.Rows[3].IsRemoved);
        Assert.Equal(new RoomKey?[] { new(1, 1), new(1, 3), new(1, 2), null, new(1, 3) }, Rooms(vm));
    }
}
