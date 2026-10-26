using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// Where this build's assemblies are loaded from, and the unpacked copies older
// versions left behind.
//
// The release is one executable. On Linux and macOS its assemblies are mapped
// straight out of it, and every client shares those pages. A Windows build is
// published with IncludeAllContentForSelfExtract (MudPlay.csproj): Windows can't map
// an assembly from inside another file, so each client would otherwise copy every
// one into memory of its own, about 90 MB a client. Instead the program unpacks
// itself once per version into the .NET extraction folder (%TEMP%\.net\MudPlay\<id>,
// or under DOTNET_BUNDLE_EXTRACT_BASE_DIR when that is set), the first launch of a
// new version taking a few seconds longer, and every client of that version loads
// the same files, which the system shares.
//
// Each version unpacks to a folder of its own, and nothing removes the old ones, so
// at startup a Windows client removes the folders of other versions that no running
// client uses (CleanOld).
public static class BundleExtraction
{
    public enum Layout
    {
        // A dev or test run: the assemblies sit beside the program, or the dotnet
        // host runs it.
        NotSingleFile,
        // Mapped from inside the single executable.
        InsideProgram,
        // Unpacked to an extraction folder and loaded from there.
        Extracted,
    }

    // An extraction folder younger than this may be one a client of another version is
    // unpacking or starting from right now; it is left for a later start.
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    private const string MainAssembly = "MudPlay.dll";
    // Loaded by every client that has opened its window, from its own extraction
    // folder, and a loaded DLL can't be deleted on Windows: the first file removed,
    // so a folder a running client uses goes no further.
    private const string NativeProbe = "libSkiaSharp.dll";
    // A folder holding any of these is one of MudPlay's: the builds before this one
    // unpacked only the two native libraries, and a folder an earlier clean-up got
    // part way through has lost its probe.
    private static readonly string[] OurFiles = [NativeProbe, "libHarfBuzzSharp.dll", MainAssembly];

    // This process. baseDirectory and processPath default to the running program's.
    public static Layout Current(string? baseDirectory = null, string? processPath = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        processPath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)
            || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Layout.NotSingleFile;
        if (!File.Exists(Path.Combine(baseDirectory, MainAssembly))) return Layout.InsideProgram;
        return SameFolder(baseDirectory, Path.GetDirectoryName(processPath)) ? Layout.NotSingleFile : Layout.Extracted;
    }

    // The startup log line and the bug report's.
    public static string Describe(Layout layout, string? baseDirectory = null) => layout switch
    {
        Layout.InsideProgram => "single-file, assemblies mapped from inside the program file",
        Layout.Extracted => $"single-file, unpacked to {baseDirectory ?? AppContext.BaseDirectory}",
        _ => "not a single-file build (assemblies loaded from beside the program)",
    };

    // At startup on Windows, when this client runs Extracted: delete the extraction
    // folders other versions of the program left beside its own. currentFolder is this
    // client's (AppContext.BaseDirectory). Only folders that hold MudPlay's own files
    // and are at least MinimumAge old are touched, and one whose files a running
    // client holds stays: its first file can't be deleted. Returns a line per folder
    // removed or left, for the program log.
    public static IReadOnlyList<string> CleanOld(string currentFolder, DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var lines = new List<string>();
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));
        if (Path.GetDirectoryName(current) is not { Length: > 0 } parent) return lines;
        string[] folders;
        // A folder we can't list must not stop startup.
        try { folders = Directory.GetDirectories(parent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return lines; }

        foreach (string folder in folders)
        {
            if (SameFolder(folder, current)) continue;
            string name = Path.GetFileName(folder);
            try
            {
                DirectoryInfo info = new(folder);
                // A link could point anywhere; an extraction is a plain folder.
                if (info.LinkTarget is not null) continue;
                if (!OurFiles.Any(f => File.Exists(Path.Combine(folder, f)))) continue;
                if (now - info.CreationTimeUtc < MinimumAge || now - info.LastWriteTimeUtc < MinimumAge) continue;
                File.Delete(Path.Combine(folder, NativeProbe));
                Directory.Delete(folder, recursive: true);
                lines.Add($"removed {name} from {parent} (an earlier version's unpacked copy)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"left {name} in {parent} for now (a client may be running it): {ex.Message}");
            }
        }
        return lines;
    }

    private static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
