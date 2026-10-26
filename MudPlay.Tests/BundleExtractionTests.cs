using System;
using System.IO;
using System.Linq;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// A Windows build unpacks itself once per version into the .NET host's extraction
// folder (<base>\MudPlay\<bundle id>) so its clients share the assemblies' pages. The
// startup clean-up removes other versions' unpacked folders there, and it must touch
// nothing else: the folder this client runs from, one another version may be
// unpacking right now, anything that isn't MudPlay's, anything outside the host's
// extraction folder, and the install itself however the extraction base is set.
public sealed class BundleExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-bundle-" + Guid.NewGuid().ToString("N"));
    private string Base => Path.Combine(_root, ".net");
    private string Parent => Path.Combine(Base, "MudPlay");
    private string Exe => Path.Combine(_root, "install", "MudPlay.exe");

    public BundleExtractionTests() => Directory.CreateDirectory(Path.Combine(_root, "install"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string Folder(string name, params string[] files) => FolderIn(Parent, name, files);

    private static string FolderIn(string parent, string name, params string[] files)
    {
        string dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        foreach (string f in files) File.WriteAllText(Path.Combine(dir, f), f);
        return dir;
    }

    private static string[] Left(string parent) => Directory.GetDirectories(parent).Select(Path.GetFileName).OfType<string>()
        .Order(StringComparer.Ordinal).ToArray();

    // Unpacked and last used a day ago, as far as the clean-up can tell.
    private static DateTime ADayOn => DateTime.UtcNow.AddDays(1);

    [Fact]
    public void CleanOld_RemovesOtherVersionsUnpackedFolders_AndLeavesItsOwnAndForeignOnes()
    {
        string current = Folder("Cur1234abcde", "MudPlay.dll", "libSkiaSharp.dll", "System.Private.CoreLib.dll");
        string old = Folder("OldVersion01", "MudPlay.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "Avalonia.Base.dll");
        FolderIn(old, "cs", "Avalonia.resources.dll");
        // An earlier clean-up got part way: MudPlay.dll is what it leaves for last.
        Folder("HalfGone0001", "MudPlay.dll");
        // Builds before this one unpacked only the native libraries: no MudPlay.dll, so
        // not known for MudPlay's.
        Folder("NativesOnly1", "libSkiaSharp.dll", "libHarfBuzzSharp.dll");
        Folder("SomeoneElses", "notes.txt");

        var lines = BundleExtraction.CleanOld(current, Exe, Base, ADayOn);

        Assert.Equal(new[] { "Cur1234abcde", "NativesOnly1", "SomeoneElses" }, Left(Parent));
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("removed ", l));
        Assert.True(File.Exists(Path.Combine(current, "MudPlay.dll")));
    }

    // Another version's client may be unpacking its folder, or starting from it, as
    // this one starts.
    [Fact]
    public void CleanOld_LeavesAFolderUnpackedWithinTheHour()
    {
        string current = Folder("Cur1234abcde", "MudPlay.dll", "libSkiaSharp.dll");
        Folder("JustUnpacked", "MudPlay.dll", "libSkiaSharp.dll");

        var lines = BundleExtraction.CleanOld(current, Exe, Base, DateTime.UtcNow + BundleExtraction.MinimumAge / 2);

        Assert.Empty(lines);
        Assert.Equal(new[] { "Cur1234abcde", "JustUnpacked" }, Left(Parent));
    }

    // A folder only the user would name so, or another program's (an Avalonia app
    // unpacks the same native libraries), is not MudPlay's.
    [Fact]
    public void CleanOld_LeavesAFolderNotNamedLikeABundle_AndAnotherAvaloniaAppsFolder()
    {
        string current = Folder("Cur1234abcde", "MudPlay.dll");
        Folder("mudplay-backup", "MudPlay.dll", "libSkiaSharp.dll");
        Folder("OtherAvalApp", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "Avalonia.Base.dll", "OtherApp.dll");

        var lines = BundleExtraction.CleanOld(current, Exe, Base, ADayOn);

        Assert.Empty(lines);
        Assert.Equal(new[] { "Cur1234abcde", "OtherAvalApp", "mudplay-backup" }, Left(Parent).Order(StringComparer.Ordinal));
    }

    // DOTNET_BUNDLE_EXTRACT_BASE_DIR set to the folder that holds the install, whose own
    // folder is named MudPlay: the extraction folder is the install folder, and its
    // bundle-like sub-folders are the user's.
    [Fact]
    public void CleanOld_WithTheExtractionBaseAboveTheInstall_TouchesNothing()
    {
        string install = Path.Combine(_root, "MudPlay");
        string exe = Path.Combine(install, "MudPlay.exe");
        string current = FolderIn(install, "Cur1234abcde", "MudPlay.dll");
        FolderIn(install, "OldVersion01", "MudPlay.dll", "libSkiaSharp.dll");

        var lines = BundleExtraction.CleanOld(current, exe, extractBase: _root, ADayOn);

        Assert.Equal(new[] { "Cur1234abcde", "OldVersion01" }, Left(install));
        Assert.Single(lines, l => l.StartsWith("older unpacked copies not looked for", StringComparison.Ordinal));
    }

    // A client whose folder isn't in the host's extraction folder for this program (a
    // layout misread, the base changed since it started) looks at nothing.
    [Fact]
    public void CleanOld_FromAFolderOutsideTheExtractionFolder_TouchesNothing()
    {
        string elsewhere = Path.Combine(_root, "elsewhere");
        string current = FolderIn(elsewhere, "Cur1234abcde", "MudPlay.dll");
        FolderIn(elsewhere, "OldVersion01", "MudPlay.dll");

        var lines = BundleExtraction.CleanOld(current, Exe, Base, ADayOn);

        Assert.Equal(new[] { "Cur1234abcde", "OldVersion01" }, Left(elsewhere));
        Assert.Single(lines);
    }

    [Fact]
    public void CleanOld_FollowsNoLink()
    {
        if (OperatingSystem.IsWindows()) return;   // a directory link needs rights there
        string current = Folder("Cur1234abcde", "MudPlay.dll", "libSkiaSharp.dll");
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "MudPlay.dll"), "x");
        Directory.CreateSymbolicLink(Path.Combine(Parent, "LinkedAway01"), elsewhere);
        // A link inside a folder that goes is removed as a link.
        string old = Folder("OldVersion01", "MudPlay.dll");
        Directory.CreateSymbolicLink(Path.Combine(old, "inside"), elsewhere);

        BundleExtraction.CleanOld(current, Exe, Base, ADayOn);

        Assert.True(File.Exists(Path.Combine(elsewhere, "MudPlay.dll")));
        Assert.False(Directory.Exists(old));
    }

    // Which layout a client runs is in its startup log and its bug report.
    [Fact]
    public void Current_TellsTheThreeLayoutsApart()
    {
        string install = Path.Combine(_root, "install");
        string unpacked = Folder("Cur1234abcde", "MudPlay.dll");

        Assert.Equal(BundleExtraction.Layout.InsideProgram, BundleExtraction.Current(install, Exe));
        Assert.Equal(BundleExtraction.Layout.Extracted, BundleExtraction.Current(unpacked, Exe));
        File.WriteAllText(Path.Combine(install, "MudPlay.dll"), "x");
        Assert.Equal(BundleExtraction.Layout.NotSingleFile, BundleExtraction.Current(install, Exe));
        Assert.Equal(BundleExtraction.Layout.NotSingleFile, BundleExtraction.Current(install, Path.Combine(install, "dotnet")));
        Assert.Contains(unpacked, BundleExtraction.Describe(BundleExtraction.Layout.Extracted, unpacked));
    }
}
