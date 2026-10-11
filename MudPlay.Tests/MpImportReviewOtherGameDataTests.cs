using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The .mp import review stays open through a switch of game data. Its rows are rooms
// of the set the file was translated on: under another set nothing is accepted (the
// save would land in that set's Loops folder), verified or translated again.
public sealed class MpImportReviewOtherGameDataTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-mpreview-otherset-" + Path.GetRandomFileName());
    private readonly string _opened = "test-mpreview-" + Guid.NewGuid().ToString("N")[..12];
    private string Other => _opened + "-other";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        foreach (string set in new[] { _opened, Other })
        {
            try
            {
                string dir = Path.Combine(AppPaths.GameDataRoot, set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // The same square on two maps, so the file's start room matches a room on each:
    // n/1 Start —N→ n/2 North —E→ n/4 Corner —S→ n/3 East —W→ n/1.
    private static string Square(int map) => $$"""
          { "Map Number": {{map}}, "Room Number": 1, "Name": "Start",
            "N": "{{map}}/2", "S": "0", "E": "{{map}}/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 2, "Name": "North",
            "N": "0", "S": "{{map}}/1", "E": "{{map}}/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 3, "Name": "East",
            "N": "{{map}}/4", "S": "0", "E": "0", "W": "{{map}}/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": {{map}}, "Room Number": 4, "Name": "Corner",
            "N": "0", "S": "{{map}}/3", "E": "0", "W": "{{map}}/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    private (MpImportReviewViewModel Review, LoopManager Loops, LogService Log) Open()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), $"[\n{Square(1)},\n{Square(2)}\n]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager g = new(cache);
        g.OnActiveSetChanged("alpha");
        string H(int room) => MegaMudHash.ComputeHashExits(g.GetRoom(new RoomKey(1, room))!);
        string[] steps = { $"{H(1)}:0000:n", $"{H(2)}:0000:e", $"{H(4)}:0000:s", $"{H(3)}:0000:w" };
        MpLoopFile file = MpFileParser.Parse(
            $"[Square][Tester]\n[SQRE:Test:Start]\n{H(1)}:{H(1)}:4:-1:0:::\n" + string.Join("\n", steps));
        LoopManager loops = new(new BfsMapper(g), g);
        loops.LoadAll(_opened);
        LogService log = new();
        return (new MpImportReviewViewModel("square.mp", file, null, new MpFileImporter(g), g, loops, filter: null, log), loops, log);
    }

    private static RoomKey?[] Rooms(MpImportReviewViewModel vm) =>
        vm.Rows.Select(r => r.ToWaypoint()?.Key).ToArray();

    private void AssertRefused(MpImportReviewViewModel review)
    {
        Assert.False(review.StatusIsGood);
        Assert.Equal(
            $"This loop's rooms belong to game data '{_opened}', and '{Other}' is active now — "
            + "switch back to that game data to change or save it.",
            review.Status);
    }

    [Fact]
    public void Accept_UnderOtherGameData_IsRefusedAndWritesNothingThere()
    {
        (MpImportReviewViewModel review, LoopManager loops, LogService log) = Open();

        loops.LoadAll(Other);
        bool closed = false;
        review.CloseRequested += _ => closed = true;
        review.AcceptCommand.Execute(null);

        AssertRefused(review);
        Assert.False(closed);
        Assert.Empty(loops.Loops);
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(Other)));
        Assert.Contains(log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "MpImporter"
            && e.Message == $"square.mp: accept refused: its rooms are game data '{_opened}''s and '{Other}' is active now");
    }

    [Fact]
    public void Accept_BackOnTheGameDataItWasOpenedOn_Saves()
    {
        (MpImportReviewViewModel review, LoopManager loops, _) = Open();
        loops.LoadAll(Other);
        review.AcceptCommand.Execute(null);
        AssertRefused(review);

        loops.LoadAll(_opened);
        Loop? accepted = null;
        review.CloseRequested += loop => accepted = loop;
        review.AcceptCommand.Execute(null);

        Assert.NotNull(accepted);
        Assert.Equal(4, loops.Get(review.LoopName.Trim())?.Waypoints.Count);
    }

    [Fact]
    public void Verify_UnderOtherGameData_IsRefusedAndTranslatesNothing()
    {
        (MpImportReviewViewModel review, LoopManager loops, _) = Open();
        // A room typed on a step re-translates every step's row on Verify.
        review.Rows[0].RoomText = "1/1";
        MpTranslatedRowViewModel[] rows = review.Rows.ToArray();

        loops.LoadAll(Other);
        review.VerifyCommand.Execute(null);

        AssertRefused(review);
        Assert.Equal(rows, review.Rows);

        loops.LoadAll(_opened);
        review.VerifyCommand.Execute(null);

        Assert.True(review.StatusIsGood);
        Assert.NotSame(rows[0], review.Rows[0]);
    }

    [Fact]
    public void AStartRoomPickedUnderOtherGameData_WaitsForTheNextVerifyOnItsOwn()
    {
        (MpImportReviewViewModel review, LoopManager loops, _) = Open();
        Assert.Equal(2, review.AnchorChoices.Count);
        MpAnchorChoice other = review.AnchorChoices.Single(a => !a.Equals(review.SelectedAnchor));
        RoomKey?[] before = Rooms(review);

        loops.LoadAll(Other);
        review.SelectedAnchor = other;

        AssertRefused(review);
        Assert.Equal(before, Rooms(review));

        loops.LoadAll(_opened);
        review.VerifyCommand.Execute(null);

        Assert.True(review.StatusIsGood);
        Assert.Equal(other.Anchor, Rooms(review)[0]);
        Assert.All(Rooms(review), k => Assert.Equal(other.Anchor.Map, k?.Map));
    }

    [Fact]
    public async Task RoomsMdIsNotLoadedUnderOtherGameData()
    {
        (MpImportReviewViewModel review, LoopManager loops, _) = Open();

        loops.LoadAll(Other);
        await review.LoadRoomsMdCommand.ExecuteAsync(null);

        AssertRefused(review);
    }
}
