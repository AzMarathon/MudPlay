using System;
using System.IO;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The Auto-Lair setup editor stays open through a switch of game data. The setup in
// it is the rooms of the set it was opened on: under another set it is neither saved
// (the save would land in that set's Loops folder) nor added to.
public sealed class LairEditorOtherGameDataTests : IDisposable
{
    private readonly string _opened = "test-laireditor-" + Guid.NewGuid().ToString("N")[..12];
    private string Other => _opened + "-other";
    private readonly LogService _log = new();
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

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 100, "Name": "Sewer A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 101, "Name": "Sewer B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static LairSetup NewSetup(string name) => new(name, new[] { new LairMarker(1, 100) });

    private string RefusalText =>
        $"This setup's rooms belong to game data '{_opened}', and '{Other}' is active now — "
        + "switch back to that game data to change or save it.";

    private (LairEditorDialogViewModel Editor, LairManager Setups) Open(LairSetup setup, bool isNew = false)
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _opened);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), RoomsJson);
        GameDataCache cache = new();
        cache.SwitchSet(_opened);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_opened);
        _timers = new LairTimerStore(cache, graph, new RoomTracker(graph));

        LairManager setups = new();
        setups.LoadAll(_opened);
        if (!isNew) setups.Save(setup);
        return (new LairEditorDialogViewModel(setup, setups, graph, _timers, isNew: isNew, log: _log), setups);
    }

    [Fact]
    public async Task Save_UnderOtherGameData_IsRefusedAndWritesNothingThere()
    {
        (LairEditorDialogViewModel editor, LairManager setups) = Open(NewSetup("Camp"));
        editor.Notes = "edited";

        setups.LoadAll(Other);
        bool closed = false;
        editor.CloseRequested += _ => closed = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(RefusalText, editor.SaveError);
        Assert.False(closed);
        Assert.Empty(setups.Setups);
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(Other)));
        Assert.Contains(_log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == "Lairs"
            && e.Message == $"setup 'Camp' not saved: its rooms are game data '{_opened}''s and '{Other}' is active now");
    }

    [Fact]
    public async Task Save_BackOnTheGameDataItWasOpenedOn_GoesThrough()
    {
        (LairEditorDialogViewModel editor, LairManager setups) = Open(NewSetup("Camp"));
        editor.Notes = "edited";
        setups.LoadAll(Other);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.HasSaveError);

        setups.LoadAll(_opened);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.HasSaveError);
        Assert.Equal("edited", setups.Get("Camp")?.Notes);
    }

    // A rename deletes the setup's old file: under another set that would be the
    // other set's setup of that name.
    [Fact]
    public async Task ARename_UnderOtherGameData_LeavesThatSetsSetupOfTheOldNameAlone()
    {
        (LairEditorDialogViewModel editor, LairManager setups) = Open(NewSetup("Camp"));
        editor.Name = "Camp moved";

        setups.LoadAll(Other);
        setups.Save(new LairSetup("Camp", new[] { new LairMarker(1, 101) }));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.HasSaveError);
        Assert.Equal(101, Assert.Single(setups.Get("Camp")!.Markers).Room);
        Assert.Null(setups.Get("Camp moved"));
    }

    [Fact]
    public async Task ANewSetup_IsNotSavedUnderOtherGameDataEither()
    {
        (LairEditorDialogViewModel editor, LairManager setups) = Open(NewSetup("Fresh"), isNew: true);

        setups.LoadAll(Other);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.HasSaveError);
        Assert.Empty(setups.Setups);
    }

    [Fact]
    public void ARoomIsNotAddedUnderOtherGameData()
    {
        (LairEditorDialogViewModel editor, LairManager setups) = Open(NewSetup("Camp"));
        editor.SelectedSearchResult = new RoomSearchResult(new RoomKey(1, 101), "Sewer B", null);

        setups.LoadAll(Other);
        editor.AddMarkerCommand.Execute(null);

        Assert.Equal(RefusalText, editor.AddMarkerError);
        Assert.Single(editor.Markers);

        setups.LoadAll(_opened);
        editor.AddMarkerCommand.Execute(null);

        Assert.False(editor.HasAddMarkerError);
        Assert.Equal(2, editor.Markers.Count);
    }
}
