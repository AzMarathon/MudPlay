using System;
using System.IO;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Which files an update puts into the folder MudPlay runs from, decided from the
// verified download before the app exits. The folder may be Downloads or the
// Desktop: a download holding anything but plain files that land directly beside
// the program must stop the update with nothing changed.
public sealed class UpdatePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-plan-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "install");
    private string NewRoot => Path.Combine(_root, "new");

    public UpdatePlanTests()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(NewRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Theory]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData("\\Windows\\System32", "absolute")]
    [InlineData("C:\\Users\\me\\photo.jpg", "absolute")]
    [InlineData("C:photo.jpg", "absolute")]
    [InlineData("..", "'..'")]
    [InlineData("../photo.jpg", "'..'")]
    [InlineData("sub\\..\\..\\x", "'..'")]
    [InlineData("notes/todo.txt", "subfolder")]
    [InlineData("notes\\todo.txt", "subfolder")]
    [InlineData("my photo.jpg", "characters")]
    [InlineData("a&b.dll", "characters")]
    [InlineData("100%.dat", "characters")]
    [InlineData("CON", "plain")]
    [InlineData("trailing.", "plain")]
    [InlineData("", "empty")]
    public void WhyNotEntry_RefusesAnythingButAPlainFileBesideTheProgram(string name, string why)
        => Assert.Contains(why, UpdatePlan.WhyNotEntry(name));

    [Theory]
    [InlineData("MudPlay")]
    [InlineData("MudPlay.exe")]
    [InlineData("lib_x-1.2+3.so")]
    public void WhyNotEntry_TakesAReleaseFileName(string name) => Assert.Null(UpdatePlan.WhyNotEntry(name));

    [Fact]
    public void Build_InstallsExactlyTheDownloadsFiles_AndNamesWhatItReplaces()
    {
        Files(Install, "MudPlay", "legacy.dat", "photo.jpg");
        Files(NewRoot, "MudPlay", "extra.dat");

        UpdatePlan plan = Build();

        // The executable goes to the helper on its own, never on the list.
        Assert.Equal("MudPlay", plan.NewExe);
        Assert.Equal("MudPlay", plan.ExeName);
        Assert.Equal(new[] { "extra.dat" }, plan.Install);
        Assert.Empty(plan.Replaced);
        Assert.Equal("replacing MudPlay; adding extra.dat; removing nothing", plan.Describe());
        Assert.Equal(plan.Install, File.ReadAllLines(plan.WriteList(_root)));
    }

    // A browser may save the program as "MudPlay (1)": the release's executable is
    // installed under that running name.
    [Fact]
    public void Build_ARenamedProgram_GetsTheReleasesExecutableUnderItsOwnName()
    {
        Files(Install, "MudPlay (1)");
        Files(NewRoot, UpdatePlan.ReleaseExeName, "extra.dat");

        UpdatePlan? plan = UpdatePlan.Build(Install, "MudPlay (1)", NewRoot, out string? refusal);

        Assert.True(plan is not null, refusal);
        Assert.Equal(UpdatePlan.ReleaseExeName, plan!.NewExe);
        Assert.Equal("MudPlay (1)", plan.ExeName);
        Assert.Equal(new[] { "extra.dat" }, plan.Install);
        Assert.Contains($"MudPlay (1) (with the new {UpdatePlan.ReleaseExeName})", plan.Describe());
    }

    [Fact]
    public void Build_RefusesWhenTheRunningProgramIsntThere_OrAnotherFileWouldTakeItsName()
    {
        Files(NewRoot, UpdatePlan.ReleaseExeName, "extra.dat");
        Assert.Null(UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? missing));
        Assert.Contains("isn't a file", missing);

        Files(Install, "extra.dat");
        Assert.Null(UpdatePlan.Build(Install, "extra.dat", NewRoot, out string? clash));
        Assert.Contains("running program's name", clash);
    }

    [Theory]
    [InlineData("subfolder")] // the download holds a folder
    [InlineData("oddname")]   // a file named with characters no release file has
    [InlineData("noexe")]     // the download has no copy of the running executable
    [InlineData("folder")]    // a folder in the install sits where one of the files goes
    [InlineData("link")]      // a link rather than a file
    public void Build_RefusesADownloadItCantInstallExactly(string fault)
    {
        Files(Install, "MudPlay");
        Files(NewRoot, "MudPlay", "extra.dat");
        switch (fault)
        {
            case "subfolder":
                Directory.CreateDirectory(Path.Combine(NewRoot, "runtimes"));
                break;
            case "oddname":
                Files(NewRoot, "my photo.jpg");
                break;
            case "noexe":
                File.Delete(Path.Combine(NewRoot, "MudPlay"));
                break;
            case "folder":
                Directory.CreateDirectory(Path.Combine(Install, "extra.dat"));
                break;
            case "link":
                File.CreateSymbolicLink(Path.Combine(NewRoot, "linked.dat"), Path.Combine(Install, "MudPlay"));
                break;
        }
        Assert.Null(UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
        // Refusing touches nothing in the install.
        Assert.Equal("MudPlay", File.ReadAllText(Path.Combine(Install, "MudPlay")));
    }

    private UpdatePlan Build()
    {
        UpdatePlan? plan = UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal);
        Assert.True(plan is not null, refusal);
        return plan!;
    }

    private static void Files(string folder, params string[] names)
    {
        foreach (string n in names) File.WriteAllText(Path.Combine(folder, n), n);
    }
}
