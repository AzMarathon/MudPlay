using System;
using System.IO;
using System.Linq;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// The startup clean-up is the only code that removes files from the folder MudPlay
// runs from, which may be Downloads or the Desktop. It may take only the exact names
// it declares, never a file of the user's that merely looks like one.
public sealed class RetiredFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-retired-" + Guid.NewGuid().ToString("N"));

    public RetiredFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Clean_RemovesOnlyTheSwapsOwnRenamedAsideExecutables()
    {
        string exe = Path.Combine(_dir, "MudPlay.exe");
        string[] mine = ["MudPlay.exe.old-123", "MudPlay.exe.old-4567"];
        string[] theirs =
        [
            "MudPlay.exe", "MudPlay.exe.new", "MudPlay.exe.old-backup", "MudPlay.exe.old-", "MudPlay.exe.old-12a",
            "Other.exe.old-123", "notes.old-1", "notes.txt", "photo.jpg",
        ];
        foreach (string f in mine.Concat(theirs)) File.WriteAllText(Path.Combine(_dir, f), f);
        // A folder that happens to carry the pattern isn't a file of ours either.
        Directory.CreateDirectory(Path.Combine(_dir, "MudPlay.exe.old-999"));
        File.WriteAllText(Path.Combine(_dir, "MudPlay.exe.old-999", "keep.txt"), "x");

        var lines = RetiredFiles.Clean(exe);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("removed MudPlay.exe.old-", l));
        Assert.Equal(theirs.Order(StringComparer.Ordinal),
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dir, "MudPlay.exe.old-999", "keep.txt")));
    }

    [Theory]
    [InlineData("MudPlay.old-1", true)]
    [InlineData("MudPlay.old-1.txt", false)]
    [InlineData("MudPlay", false)]
    [InlineData("photo.jpg", false)]
    public void IsRetired_TiesThePatternToTheExecutablesOwnName(string name, bool retired)
        => Assert.Equal(retired, RetiredFiles.IsRetired(name, "MudPlay"));
}
