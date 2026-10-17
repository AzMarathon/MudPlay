using System;
using System.IO;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Renaming a loop in the Edit Loop window saves it under the new name and only then
// drops the old one, so a save that fails loses nothing.
public sealed class LoopEditorRenameTests : IDisposable
{
    private readonly string _set = "test-rename-" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try
        {
            string dir = Path.Combine(AppPaths.GameDataRoot, _set);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    private string LoopsRoot => AppPaths.GameDataSetLoopsFolder(_set);

    private (LoopEditorDialogViewModel Editor, LoopManager Loops) Open(string name)
    {
        RoomGraphManager graph = new(new GameDataCache());
        LoopManager loops = new(new BfsMapper(graph), graph);
        loops.LoadAll(_set);
        Loop loop = new(name, new[] { new LoopWaypoint(new RoomKey(1, 1)), new LoopWaypoint(new RoomKey(1, 2)) })
        {
            Notes = "before",
        };
        Assert.True(loops.Save(loop));
        return (new LoopEditorDialogViewModel(loop, loops, graph), loops);
    }

    [Fact]
    public async Task ARenameWhoseSaveFails_LeavesTheOldLoopInPlace()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open("Circuit");
        // A folder where the new file would go: the write fails after the old
        // name's file could already have been deleted.
        Directory.CreateDirectory(Path.Combine(LoopsRoot, "Renamed" + LoopManager.LoopFileSuffix));
        editor.Name = "Renamed";
        editor.Notes = "after";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Loop not saved:", editor.SaveError);
        Assert.True(File.Exists(Path.Combine(LoopsRoot, "Circuit" + LoopManager.LoopFileSuffix)));
        Loop? kept = loops.Get("Circuit");
        Assert.NotNull(kept);
        Assert.Equal("Circuit", kept.Name);
        Assert.Equal("before", kept.Notes);
        Assert.Null(loops.Get("Renamed"));
    }

    [Fact]
    public async Task ARenameThatSaves_DropsTheOldLoop()
    {
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open("Circuit");
        editor.Name = "Renamed";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.HasSaveError);
        Assert.Null(loops.Get("Circuit"));
        Assert.False(File.Exists(Path.Combine(LoopsRoot, "Circuit" + LoopManager.LoopFileSuffix)));
        Assert.True(File.Exists(Path.Combine(LoopsRoot, "Renamed" + LoopManager.LoopFileSuffix)));
        Assert.Equal("Renamed", loops.Get("Renamed")?.Name);
    }

    [Fact]
    public async Task ARenameToANameOfTheSameFile_KeepsTheFile()
    {
        // '/' can't be in a file name anywhere, so both names are the file a_b.loop.
        (LoopEditorDialogViewModel editor, LoopManager loops) = Open("a/b");
        editor.Name = "a_b";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.HasSaveError);
        Assert.Null(loops.Get("a/b"));
        Assert.True(File.Exists(Path.Combine(LoopsRoot, "a_b" + LoopManager.LoopFileSuffix)));
        Assert.Equal("a_b", loops.Get("a_b")?.Name);
    }
}
