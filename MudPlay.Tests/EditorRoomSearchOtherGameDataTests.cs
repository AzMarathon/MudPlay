using System;
using System.IO;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The add-room box of the Edit Loop window and the Auto-Lair setup editor lists the
// active game data's rooms. While another set is active than the one the window was
// opened on it lists none, and nothing listed or highlighted is kept through a
// refused add, so a room of the other set can't be taken once back on the window's own.
public sealed class EditorRoomSearchOtherGameDataTests : IDisposable
{
    private readonly string _opened = "test-roomsearch-" + Guid.NewGuid().ToString("N")[..12];
    private string Other => _opened + "-other";
    private LairTimerStore? _timers;

    public void Dispose()
    {
        _timers?.Dispose();
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

    private static string Room(int map, int room, string name) => $$"""
          { "Map Number": {{map}}, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    private static void WriteSet(string set, params string[] rooms)
    {
        string root = Path.Combine(AppPaths.GameDataRoot, set);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Rooms.json"), "[\n" + string.Join(",\n", rooms) + "\n]");
    }

    // The window's own set has Sewer A and Sewer B; the other has a cave the first
    // lacks. Returns the cache and graph on the window's own set.
    private (GameDataCache Cache, RoomGraphManager Graph) OwnSetLoaded()
    {
        WriteSet(_opened, Room(1, 100, "Sewer A"), Room(1, 101, "Sewer B"));
        WriteSet(Other, Room(1, 100, "Sewer A"), Room(9, 900, "Other Cave"));
        GameDataCache cache = new();
        cache.SwitchSet(_opened);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_opened);
        return (cache, graph);
    }

    private static void Switch(GameDataCache cache, RoomGraphManager graph, string set)
    {
        cache.SwitchSet(set);
        graph.OnActiveSetChanged(set);
    }

    private (LairEditorDialogViewModel Editor, LairManager Setups, GameDataCache Cache, RoomGraphManager Graph) OpenLairEditor()
    {
        (GameDataCache cache, RoomGraphManager graph) = OwnSetLoaded();
        _timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        LairManager setups = new();
        setups.LoadAll(_opened);
        LairSetup setup = new("Camp", new[] { new LairMarker(1, 100) });
        setups.Save(setup);
        return (new LairEditorDialogViewModel(setup, setups, graph, _timers), setups, cache, graph);
    }

    private (LoopEditorDialogViewModel Editor, LoopManager Loops, GameDataCache Cache, RoomGraphManager Graph) OpenLoopEditor(
        OpenedOnGameData? roomsOf = null)
    {
        (GameDataCache cache, RoomGraphManager graph) = OwnSetLoaded();
        LoopManager loops = new(new BfsMapper(graph), graph);
        loops.LoadAll(_opened);
        Loop loop = new("Circuit", new[] { new LoopWaypoint(new RoomKey(1, 100)), new LoopWaypoint(new RoomKey(1, 101)) });
        loops.Save(loop);
        return (new LoopEditorDialogViewModel(loop, loops, graph, roomsOf: roomsOf), loops, cache, graph);
    }

    // ----- Auto-Lair setup editor ---------------------------------------------------

    [Fact]
    public async Task LairEditor_ListsNoRoomsOfTheOtherGameData_AndTakesNoneOnceBack()
    {
        (LairEditorDialogViewModel editor, LairManager setups, GameDataCache cache, RoomGraphManager graph) = OpenLairEditor();

        Switch(cache, graph, Other);
        setups.LoadAll(Other);
        editor.RebuildSearchResults("Other Cave");

        Assert.Empty(editor.SearchResults);
        Assert.Contains($"belong to game data '{_opened}'", editor.AddMarkerError);

        Switch(cache, graph, _opened);
        setups.LoadAll(_opened);
        editor.AddMarkerCommand.Execute(null);

        Assert.Single(editor.Markers);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(setups.Get("Camp")!.Markers, m => m.Map == 9);
    }

    [Fact]
    public void LairEditor_RowsListedBeforeTheSwitch_AreDroppedByARefusedAdd()
    {
        (LairEditorDialogViewModel editor, LairManager setups, GameDataCache cache, RoomGraphManager graph) = OpenLairEditor();
        editor.RebuildSearchResults("Sewer B");
        Assert.Single(editor.SearchResults);

        Switch(cache, graph, Other);
        setups.LoadAll(Other);
        editor.AddMarkerCommand.Execute(null);

        Assert.Empty(editor.SearchResults);
        Assert.False(editor.HasSearchResults);
        Assert.Null(editor.SelectedSearchResult);
        Assert.Single(editor.Markers);
    }

    [Fact]
    public void LairEditor_OnItsOwnGameData_ListsAndAddsAsBefore()
    {
        (LairEditorDialogViewModel editor, _, _, _) = OpenLairEditor();

        editor.RebuildSearchResults("Sewer B");
        RoomSearchResult row = Assert.Single(editor.SearchResults);
        editor.AddMarkerCommand.Execute(null);

        Assert.Equal(new RoomKey(1, 101), row.Key);
        Assert.Equal(2, editor.Markers.Count);
        Assert.Equal(new RoomKey(1, 101), editor.Markers[1].Key);
    }

    // ----- Edit Loop ----------------------------------------------------------------

    [Fact]
    public void EditLoop_ListsNoRoomsOfTheOtherGameData_AndTakesNoneOnceBack()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops, GameDataCache cache, RoomGraphManager graph) = OpenLoopEditor();

        Switch(cache, graph, Other);
        loops.LoadAll(Other);
        editor.RebuildSearchResults("Other Cave");

        Assert.Empty(editor.SearchResults);
        Assert.Contains($"belong to game data '{_opened}'", editor.AddWaypointError);

        Switch(cache, graph, _opened);
        loops.LoadAll(_opened);
        editor.AddWaypointCommand.Execute(null);

        Assert.Equal(2, editor.Waypoints.Count);
    }

    [Fact]
    public void EditLoop_RowsListedBeforeTheSwitch_AreDroppedByARefusedAdd()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops, GameDataCache cache, RoomGraphManager graph) = OpenLoopEditor();
        editor.RebuildSearchResults("Sewer B");
        editor.SelectedSearchResult = Assert.Single(editor.SearchResults);

        Switch(cache, graph, Other);
        loops.LoadAll(Other);
        editor.AddWaypointCommand.Execute(null);

        Assert.Empty(editor.SearchResults);
        Assert.False(editor.HasSearchResults);
        Assert.Null(editor.SelectedSearchResult);
        Assert.Equal(2, editor.Waypoints.Count);
    }

    [Fact]
    public void EditLoop_OnItsOwnGameData_ListsAndAddsAsBefore()
    {
        (LoopEditorDialogViewModel editor, _, _, _) = OpenLoopEditor();

        editor.RebuildSearchResults("Sewer B");
        Assert.Single(editor.SearchResults);
        editor.AddWaypointCommand.Execute(null);

        Assert.Equal(3, editor.Waypoints.Count);
        Assert.Equal(new RoomKey(1, 101), editor.Waypoints[2].Key);
    }

    // A loop can be running that was started on other game data, so the Save chip opens
    // it as the set's it was started on, whatever set is active by then.
    [Fact]
    public async Task EditLoop_ALoopOpenedAsAnotherGameDatas_IsNotSaved()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops, _, _) = OpenLoopEditor(roomsOf: new OpenedOnGameData(Other));
        editor.Name = "From the run";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            $"This loop's rooms belong to game data '{Other}', and '{_opened}' is active now — "
            + "switch back to that game data to change or save it.",
            editor.SaveError);
        Assert.Null(loops.Get("From the run"));
    }

    // A loop stamped while no game data was active is not the active set's: "no
    // set" is a stamp, and only no stamp at all means the catalogue's own set.
    [Fact]
    public async Task EditLoop_ALoopStampedWithNoGameData_IsNotSavedUnderTheActiveOne()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops, _, _) = OpenLoopEditor(roomsOf: new OpenedOnGameData(null));
        editor.Name = "From no set";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            $"This loop's rooms belong to game data '(none)', and '{_opened}' is active now — "
            + "switch back to that game data to change or save it.",
            editor.SaveError);
        Assert.Null(loops.Get("From no set"));
    }
}
