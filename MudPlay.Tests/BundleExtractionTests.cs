using System;
using System.IO;
using System.Linq;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// A Windows build unpacks itself once per version into the .NET extraction folder
// (<base>\MudPlay\<bundle id>) so its clients share the assemblies' pages. The
// startup clean-up removes other versions' unpacked folders there, and it must touch
// nothing else: the folder this client runs from, one another version may be
// unpacking right now, and anything that isn't MudPlay's.
public sealed class BundleExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-bundle-" + Guid.NewGuid().ToString("N"));
    private string Parent => Path.Combine(_root, ".net", "MudPlay");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string Folder(string name, params string[] files)
    {
        string dir = Path.Combine(Parent, name);
        Directory.CreateDirectory(dir);
        foreach (string f in files) File.WriteAllText(Path.Combine(dir, f), f);
        return dir;
    }

    private string[] Left() => Directory.GetDirectories(Parent).Select(Path.GetFileName).OfType<string>()
        .Order(StringComparer.Ordinal).ToArray();

    // Unpacked a day ago, as far as the clean-up can tell.
    private static DateTime ADayOn => DateTime.UtcNow.AddDays(1);

    [Fact]
    public void CleanOld_RemovesOtherVersionsUnpackedFolders_AndLeavesItsOwnAndForeignOnes()
    {
        string current = Folder("Cur1234abcde", "MudPlay.dll", "libSkiaSharp.dll", "System.Private.CoreLib.dll");
        Folder("OldVersion01", "MudPlay.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "Avalonia.Base.dll");
        // Builds before this one unpacked only the native libraries.
        Folder("NativesOnly1", "libSkiaSharp.dll", "libHarfBuzzSharp.dll");
        // An earlier clean-up got part way: the probe went, the rest stayed.
        Folder("HalfGone0001", "MudPlay.dll", "Avalonia.Base.dll");
        Folder("SomeoneElses", "notes.txt");

        var lines = BundleExtraction.CleanOld(current, ADayOn);

        Assert.Equal(new[] { "Cur1234abcde", "SomeoneElses" }, Left());
        Assert.Equal(3, lines.Count);
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

        var lines = BundleExtraction.CleanOld(current, DateTime.UtcNow + BundleExtraction.MinimumAge / 2);

        Assert.Empty(lines);
        Assert.Equal(new[] { "Cur1234abcde", "JustUnpacked" }, Left());
    }

    [Fact]
    public void CleanOld_FollowsNoLink()
    {
        if (OperatingSystem.IsWindows()) return;   // a directory link needs rights there
        string current = Folder("Cur1234abcde", "MudPlay.dll", "libSkiaSharp.dll");
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "libSkiaSharp.dll"), "x");
        Directory.CreateSymbolicLink(Path.Combine(Parent, "LinkedAway01"), elsewhere);

        BundleExtraction.CleanOld(current, ADayOn);

        Assert.True(File.Exists(Path.Combine(elsewhere, "libSkiaSharp.dll")));
    }

    // Which layout a client runs is in its startup log and its bug report.
    [Fact]
    public void Current_TellsTheThreeLayoutsApart()
    {
        string install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        string exe = Path.Combine(install, "MudPlay.exe");
        string unpacked = Folder("Cur1234abcde", "MudPlay.dll");

        Assert.Equal(BundleExtraction.Layout.InsideProgram, BundleExtraction.Current(install, exe));
        Assert.Equal(BundleExtraction.Layout.Extracted, BundleExtraction.Current(unpacked, exe));
        File.WriteAllText(Path.Combine(install, "MudPlay.dll"), "x");
        Assert.Equal(BundleExtraction.Layout.NotSingleFile, BundleExtraction.Current(install, exe));
        Assert.Equal(BundleExtraction.Layout.NotSingleFile, BundleExtraction.Current(install, Path.Combine(install, "dotnet")));
        Assert.Contains(unpacked, BundleExtraction.Describe(BundleExtraction.Layout.Extracted, unpacked));
    }
}
