using System;
using System.IO;
using System.Linq;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Which files an update may touch in the folder MudPlay runs from, decided from the
// old and new manifests before the app exits. The folder may be Downloads or the
// Desktop: a manifest line that could name anything outside MudPlay's own files
// must stop the update with nothing changed.
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

    // ----- InstallManifest ------------------------------------------------------

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
    public void Parse_RefusesAnyLineThatIsntAPlainFileBesideTheProgram(string line, string why)
    {
        Assert.Null(InstallManifest.Parse($"MudPlay\n{line}\n", Install, out string? error));
        Assert.Contains("line 2", error);
        Assert.Contains(why, error);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlanks_DropsCaseDuplicates_KeepsOrder()
    {
        var entries = InstallManifest.Parse("# header\r\n\r\nMudPlay.exe\r\nMudPlay.files\r\nmudplay.EXE\r\nlib_x-1.2+3.so\r\n",
            Install, out string? error);
        Assert.Null(error);
        Assert.Equal(new[] { "MudPlay.exe", "MudPlay.files", "lib_x-1.2+3.so" }, entries);
    }

    [Fact]
    public void Parse_RefusesAnEmptyList()
    {
        Assert.Null(InstallManifest.Parse("# nothing\n", Install, out string? error));
        Assert.Contains("lists no files", error);
    }

    [Fact]
    public void Write_ListsItsFilesAndItself_AndReadsBack()
    {
        InstallManifest.Write(Install, ["MudPlay"]);
        ManifestRead m = InstallManifest.Read(Install);
        Assert.True(m.Present);
        Assert.Equal(new[] { "MudPlay", InstallManifest.FileName }, m.Entries);
        Assert.False(InstallManifest.Read(NewRoot).Present);
    }

    // ----- UpdatePlan -----------------------------------------------------------

    [Fact]
    public void Build_InstallsTheNewList_RemovesOnlyWhatTheOldListDropped()
    {
        Files(Install, "MudPlay", "legacy.dat", "kept.dat", "photo.jpg", "other.exe");
        Directory.CreateDirectory(Path.Combine(Install, "notes"));
        InstallManifest.Write(Install, ["MudPlay", "legacy.dat", "kept.dat", "gone-already.dat"]);
        Files(NewRoot, "MudPlay", "kept.dat", "extra.dat");
        InstallManifest.Write(NewRoot, ["MudPlay", "kept.dat", "extra.dat"]);

        UpdatePlan plan = Build();

        Assert.Equal(new[] { "MudPlay", "kept.dat", "extra.dat", InstallManifest.FileName }, plan.Install);
        // gone-already.dat isn't there to remove; the user's files are on no list.
        Assert.Equal(new[] { "legacy.dat" }, plan.Remove);
        Assert.True(plan.HadOldManifest);
        Assert.Equal(new[] { "MudPlay", "kept.dat", InstallManifest.FileName }, plan.Replaced);
        Assert.Contains("removing legacy.dat", plan.Describe());
        Assert.Contains("adding extra.dat", plan.Describe());

        (string installList, string removeList) = plan.WriteLists(_root);
        Assert.Equal(plan.Install, File.ReadAllLines(installList));
        Assert.Equal(plan.Remove, File.ReadAllLines(removeList));
    }

    // Before manifests, nothing in the folder can be known to be MudPlay's.
    [Fact]
    public void Build_PreManifestInstall_RemovesNothing()
    {
        Files(Install, "MudPlay", "legacy.dat", "photo.jpg");
        Files(NewRoot, "MudPlay");
        InstallManifest.Write(NewRoot, ["MudPlay"]);

        UpdatePlan plan = Build();

        Assert.Empty(plan.Remove);
        Assert.False(plan.HadOldManifest);
        Assert.Contains("no file list beside the old build", plan.Describe());
    }

    // A file the old list names in a different case is the same file on Windows and
    // macOS: kept, never removed.
    [Fact]
    public void Build_NeverRemovesAFileTheNewListNamesInAnotherCase()
    {
        Files(Install, "MudPlay", "Data.pak");
        InstallManifest.Write(Install, ["MudPlay", "Data.pak"]);
        Files(NewRoot, "MudPlay", "data.pak");
        InstallManifest.Write(NewRoot, ["MudPlay", "data.pak"]);
        Assert.Empty(Build().Remove);
    }

    [Fact]
    public void Build_NewBuildWithoutAManifest_GetsOneFromItsOwnFiles()
    {
        Files(Install, "MudPlay");
        Files(NewRoot, "MudPlay");

        UpdatePlan plan = Build();

        Assert.Equal(new[] { "MudPlay", InstallManifest.FileName }, plan.Install);
        Assert.Equal(new[] { "MudPlay", InstallManifest.FileName }, InstallManifest.Read(NewRoot).Entries);
    }

    [Fact]
    public void Build_RefusesADamagedOldManifest_BeforeAndAfterTheDownload()
    {
        Files(Install, "MudPlay");
        File.WriteAllText(Path.Combine(Install, InstallManifest.FileName), "MudPlay\n../../Documents/taxes.pdf\n");
        Files(NewRoot, "MudPlay");
        InstallManifest.Write(NewRoot, ["MudPlay"]);

        Assert.Contains("damaged", UpdatePlan.WhyNotFromHere(Install));
        Assert.Null(UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal));
        Assert.Contains("'..'", refusal);
        Assert.Contains("DAMAGED", UpdatePlan.DescribeInstall(Install));
    }

    [Fact]
    public void Build_RefusesADamagedNewManifest()
    {
        Files(Install, "MudPlay");
        Files(NewRoot, "MudPlay");
        File.WriteAllText(Path.Combine(NewRoot, InstallManifest.FileName), "MudPlay\n/home/me/photo.jpg\n");
        Assert.Null(UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal));
        Assert.Contains("absolute", refusal);
    }

    [Theory]
    [InlineData("missing")]   // the list names a file the build lacks
    [InlineData("noexe")]     // the build has no copy of the running executable
    [InlineData("folder")]    // a folder sits where one of the files goes
    [InlineData("subfolder")] // a build without a list that has folders
    public void Build_RefusesABuildItCantInstallExactly(string fault)
    {
        Files(Install, "MudPlay");
        Files(NewRoot, "MudPlay", "extra.dat");
        switch (fault)
        {
            case "missing":
                InstallManifest.Write(NewRoot, ["MudPlay", "extra.dat", "absent.dat"]);
                break;
            case "noexe":
                InstallManifest.Write(NewRoot, ["extra.dat"]);
                break;
            case "folder":
                InstallManifest.Write(NewRoot, ["MudPlay", "extra.dat"]);
                Directory.CreateDirectory(Path.Combine(Install, "extra.dat"));
                break;
            case "subfolder":
                Directory.CreateDirectory(Path.Combine(NewRoot, "runtimes"));
                break;
        }
        Assert.Null(UpdatePlan.Build(Install, "MudPlay", NewRoot, out string? refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
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
