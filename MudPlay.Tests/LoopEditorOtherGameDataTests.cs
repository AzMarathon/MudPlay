using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The Edit Loop window stays open through a switch of game data. The loop in it is
// the rooms of the set it was opened on: under another set it is neither saved (the
// save would land in that set's Loops folder) nor added to.
public sealed class LoopEditorOtherGameDataTests : IDisposable
{
    private readonly string _opened = "test-editor-" + Guid.NewGuid().ToString("N")[..12];
    private string Other => _opened + "-other";

    public void Dispose()
    {
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

    private static Loop NewLoop(string name) =>
        new(name, new[] { new LoopWaypoint(new RoomKey(1, 1)), new LoopWaypoint(new RoomKey(1, 2)) });

    private (LoopEditorDialogViewModel Editor, LoopManager Loops) Open(Loop loop, bool isNew = false)
    {
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager loops = new(new BfsMapper(graph), graph);
        loops.LoadAll(_opened);
        if (!isNew) loops.Save(loop);
        return (new LoopEditorDialogViewModel(loop, loops, graph, isNew: isNew), loops);
    }

    [Fact]
    public async Task Save_UnderOtherGameData_IsRefusedAndWritesNothingThere()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open(NewLoop("Circuit"));
        editor.Notes = "edited";

        loops.LoadAll(Other);
        Loop? closedWith = NewLoop("unset");
        editor.CloseRequested += loop => closedWith = loop;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Contains($"belong to game data '{_opened}'", editor.SaveError);
        Assert.Contains($"'{Other}' is active now", editor.SaveError);
        Assert.Equal("unset", closedWith?.Name);
        Assert.Empty(loops.Loops);
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(Other)));
    }

    [Fact]
    public async Task Save_BackOnTheGameDataItWasOpenedOn_GoesThrough()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open(NewLoop("Circuit"));
        editor.Notes = "edited";
        loops.LoadAll(Other);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.HasSaveError);

        loops.LoadAll(_opened);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("edited", loops.Get("Circuit")?.Notes);
    }

    [Fact]
    public async Task ANewLoop_IsNotSavedUnderOtherGameDataEither()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open(NewLoop("Fresh"), isNew: true);

        loops.LoadAll(Other);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.HasSaveError);
        Assert.Empty(loops.Loops);
    }

    [Fact]
    public void ARoomIsNotAddedUnderOtherGameData()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open(NewLoop("Circuit"));
        int rooms = editor.Waypoints.Count;

        loops.LoadAll(Other);
        editor.AddWaypointByRoomKey(new RoomKey(1, 1));
        Assert.Contains($"belong to game data '{_opened}'", editor.AddWaypointError);
        editor.AddWaypointError = string.Empty;
        editor.AddWaypointCommand.Execute(null);

        Assert.Equal(rooms, editor.Waypoints.Count);
        Assert.Contains($"belong to game data '{_opened}'", editor.AddWaypointError);
    }
}
