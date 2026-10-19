using System;
using System.IO;
using System.Linq;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// The startup and pre-update clean-ups are the only code that removes files from
// the folder MudPlay runs from, which may be Downloads or the Desktop. They may take
// only the exact names they declare, never a file of the user's that merely looks
// like one.
public sealed class RetiredFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-retired-" + Guid.NewGuid().ToString("N"));

    public RetiredFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private static readonly string[] Theirs =
    [
        "MudPlay.exe", "MudPlay.exe.new", "MudPlay.exe.old-backup", "MudPlay.exe.old-", "MudPlay.exe.old-12a",
        "Other.exe.old-123", "notes.old-1", "notes.txt", "photo.jpg", "MudPlay.exe.mudplay-update-x1",
        "photo.jpg.mudplay-update-123",
    ];

    private void Make(params string[] names)
    {
        foreach (string f in names) File.WriteAllText(Path.Combine(_dir, f), f);
    }

    private string[] Left() => Directory.GetFiles(_dir).Select(Path.GetFileName).OfType<string>()
        .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Clean_OnWindows_RemovesOnlyTheSwapsOwnRenamedAsideExecutables()
    {
        string[] mine = ["MudPlay.exe.old-123", "MudPlay.exe.old-4567"];
        Make([.. mine, .. Theirs, "MudPlay.exe.mudplay-update-77"]);
        // A folder that happens to carry the pattern isn't a file of ours either.
        Directory.CreateDirectory(Path.Combine(_dir, "MudPlay.exe.old-999"));
        File.WriteAllText(Path.Combine(_dir, "MudPlay.exe.old-999", "keep.txt"), "x");

        var lines = RetiredFiles.Clean(Path.Combine(_dir, "MudPlay.exe"), windows: true);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("removed MudPlay.exe.old-", l));
        // The swap's abandoned temporary copy waits for the next update.
        Assert.Equal(Theirs.Append("MudPlay.exe.mudplay-update-77").Order(StringComparer.Ordinal), Left());
        Assert.True(File.Exists(Path.Combine(_dir, "MudPlay.exe.old-999", "keep.txt")));
    }

    // Only Windows renames the executable aside, so elsewhere a file of that name is
    // the user's.
    [Fact]
    public void Clean_ElsewhereThanWindows_LeavesOldNamedFiles()
    {
        Make("MudPlay", "MudPlay.old-1", "notes.old-1");
        Assert.Empty(RetiredFiles.Clean(Path.Combine(_dir, "MudPlay"), windows: false));
        Assert.Equal(new[] { "MudPlay", "MudPlay.old-1", "notes.old-1" }, Left());
    }

    // With no executable beside it, a renamed-aside copy may be the only program
    // left (a swap that couldn't put the old one back): it stays.
    [Fact]
    public void Clean_KeepsTheRenamedAsideCopy_WhenTheExecutableIsMissing()
    {
        Make("MudPlay.exe.old-123", "MudPlay.exe.mudplay-update-5");
        Assert.Empty(RetiredFiles.Clean(Path.Combine(_dir, "MudPlay.exe"), windows: true));
        Assert.Empty(RetiredFiles.CleanAbandonedSwapCopies(Path.Combine(_dir, "MudPlay.exe")));
        Assert.Equal(new[] { "MudPlay.exe.mudplay-update-5", "MudPlay.exe.old-123" }, Left());
    }

    // Before an update: a killed helper's temporary copy of the new executable goes,
    // matched exactly; nothing else does.
    [Fact]
    public void CleanAbandonedSwapCopies_RemovesOnlyTheExecutablesTemporaryCopies()
    {
        Make("MudPlay", "MudPlay.mudplay-update-4242", "MudPlay.mudplay-update-", "MudPlay.mudplay-update-12x",
            "photo.jpg.mudplay-update-123", "MudPlay.old-1");

        var lines = RetiredFiles.CleanAbandonedSwapCopies(Path.Combine(_dir, "MudPlay"));

        Assert.Equal(new[] { "removed MudPlay.mudplay-update-4242" }, lines.Select(l => l.Split(" from ")[0]));
        Assert.Equal(new[] { "MudPlay", "MudPlay.mudplay-update-", "MudPlay.mudplay-update-12x", "MudPlay.old-1",
            "photo.jpg.mudplay-update-123" }, Left());
    }

    [Theory]
    [InlineData("MudPlay.old-1", true, true)]
    [InlineData("MudPlay.old-1", false, false)]
    [InlineData("MudPlay.old-1.txt", true, false)]
    [InlineData("MudPlay", true, false)]
    [InlineData("photo.jpg", true, false)]
    public void IsRetired_TiesThePatternToTheExecutablesOwnName_OnWindowsOnly(string name, bool windows, bool retired)
        => Assert.Equal(retired, RetiredFiles.IsRetired(name, "MudPlay", windows));
}
