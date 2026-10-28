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
// client uses (CleanOld), within the host's extraction folder for this program only.
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

    // Everything a Windows bundle unpacked before 3.186.9, when only native libraries
    // were (IncludeNativeLibrariesForSelfExtract): the Skia and HarfBuzz renderers and
    // Avalonia's ANGLE, as the bundle manifest of a 3.186.4 win-x64 publish lists them.
    // A bundle-named folder holding a renderer and nothing outside these is an old
    // MudPlay's unpacked copy. Declared before InUseProbes, which is built from it.
    private static readonly string[] OldBundleNatives = ["libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"];

    // What a client of a version maps from its folder as it starts, earliest first: the
    // runtime's own library before any of the app runs, the app, the native renderer as
    // the window opens (all a pre-3.186.9 client has there). A file Windows has mapped
    // can't be opened for writing, so one that refuses an exclusive open marks a folder
    // in use. Left uncovered: a client whose host has found the folder and not yet
    // mapped the first of them, a few milliseconds of its start; MinimumAge keeps a
    // folder unpacked just now out of it.
    private static readonly string[] InUseProbes = ["System.Private.CoreLib.dll", MainAssembly, .. OldBundleNatives];

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

    // The folder the .NET host unpacks every version of this program into:
    // <DOTNET_BUNDLE_EXTRACT_BASE_DIR, else the temp folder's .net>\<program name>.
    public static string ExtractionParent(string exePath, string? extractBase) =>
        Path.Combine(extractBase ?? Path.Combine(Path.GetTempPath(), ".net"), Path.GetFileNameWithoutExtension(exePath));

    // At startup on Windows, when this client runs Extracted: delete the folders other
    // versions of the program unpacked beside its own. Nothing is touched unless this
    // client's folder is a bundle folder directly in the host's extraction folder for
    // this program, and that folder holds neither the program nor its folder. A
    // candidate must be a plain folder (no link), named like a bundle id, at least
    // MinimumAge old, not read within that, and with none of its first-mapped files in
    // use; a link inside one is removed, never followed. Two kinds qualify:
    //
    //  - Every start: a folder holding MudPlay.dll (3.186.9 on). The cost is one
    //    listing of the extraction folder and a look for MudPlay.dll in each sibling.
    //  - Once per install: a folder holding only the native libraries a version before
    //    3.186.9 unpacked (OldBundleNatives). doneMarker (in the app's data) says the
    //    pass is done; it is written only after a pass that left none behind, so one
    //    still in use is tried again at the next start. Of clients starting together,
    //    the one that takes doneMarker's lock file does the pass; the rest skip it.
    //
    // currentFolder is this client's (AppContext.BaseDirectory); exePath, extractBase
    // and doneMarker default to the program, DOTNET_BUNDLE_EXTRACT_BASE_DIR and
    // AppPaths.LegacyUnpackedCopiesRemovedMarker. Returns a line per folder removed or
    // left, what the one-time pass did, or why nothing was looked at, for the program log.
    public static IReadOnlyList<string> CleanOld(string currentFolder, string? exePath = null, string? extractBase = null,
        DateTime? nowUtc = null, string? doneMarker = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var lines = new List<string>();
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || Path.GetDirectoryName(Path.GetFullPath(exePath)) is not { Length: > 0 } exeDir)
            return lines;
        extractBase ??= Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } set ? set : null;
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExtractionParent(exePath, extractBase)));
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));

        if (!LooksLikeBundleId(Path.GetFileName(current)) || !SameFolder(Path.GetDirectoryName(current), parent))
        {
            lines.Add($"older unpacked copies not looked for: this client runs from {current}, not from a bundle folder in {parent}");
            return lines;
        }
        if (AtOrUnder(exeDir, parent) || AtOrUnder(parent, exeDir))
        {
            lines.Add($"older unpacked copies not looked for: the extraction folder {parent} and the program's folder {exeDir} overlap");
            return lines;
        }
        DirectoryInfo[] folders;
        // A folder we can't list must not stop startup.
        try
        {
            DirectoryInfo parentInfo = new(parent);
            if (parentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                lines.Add($"older unpacked copies not looked for: {parent} is a link");
                return lines;
            }
            folders = parentInfo.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return lines; }

        doneMarker ??= AppPaths.LegacyUnpackedCopiesRemovedMarker;
        using FileStream? legacyLock = File.Exists(doneMarker) ? null : TakeLock(doneMarker + ".lock", lines);
        int legacyRemoved = 0, legacyLeft = 0;
        foreach (DirectoryInfo folder in folders)
        {
            string name = folder.Name;
            if (SameFolder(folder.FullName, current) || !LooksLikeBundleId(name)) continue;
            bool legacy = false;
            try
            {
                // A link could point anywhere; an unpacked copy is a plain folder.
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                FileInfo main = new(Path.Combine(folder.FullName, MainAssembly));
                FileInfo[]? natives = main.Exists || legacyLock is null ? null : OldNativesOnly(folder);
                if (!main.Exists && natives is null) continue;
                legacy = natives is not null;
                // Last read within the hour: a client may still be running it.
                FileInfo[] used = natives ?? [main];
                if (now - folder.CreationTimeUtc < MinimumAge || now - folder.LastWriteTimeUtc < MinimumAge
                    || used.Any(f => now - f.LastAccessTimeUtc < MinimumAge))
                {
                    if (legacy)
                    {
                        legacyLeft++;
                        lines.Add($"left {name} in {parent} for now: unpacked or read within the hour");
                    }
                    continue;
                }
                if (InUseProbes.FirstOrDefault(p => !OpensExclusively(Path.Combine(folder.FullName, p))) is { } held)
                {
                    if (legacy) legacyLeft++;
                    lines.Add($"left {name} in {parent} for now: a running client holds its {held}");
                    continue;
                }
                DeleteTree(folder);
                if (legacy) legacyRemoved++;
                lines.Add(legacy
                    ? $"removed {name} from {parent} (a pre-3.186.9 version's unpacked native libraries)"
                    : $"removed {name} from {parent} (an earlier version's unpacked copy)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (legacy) legacyLeft++;
                lines.Add($"left {name} in {parent} for now (a client may be running it): {ex.Message}");
            }
        }
        if (legacyLock is not null)
            lines.Add(FinishLegacyPass(doneMarker, legacyRemoved, legacyLeft));
        return lines;
    }

    // The one-time pass's lock: a file only one client can create; it goes when that
    // client closes it. Null (and a line saying so) when another client holds it.
    private static FileStream? TakeLock(string path, List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add("one-time removal of pre-3.186.9 unpacked copies: another client is doing it; skipped this start");
            return null;
        }
    }

    private static string FinishLegacyPass(string doneMarker, int removed, int left)
    {
        string done = $"one-time removal of pre-3.186.9 unpacked copies ran: {removed} removed, {left} left";
        if (left > 0) return done + "; it runs again at the next start";
        try
        {
            File.WriteAllText(doneMarker, $"done {DateTime.UtcNow:O}\n");
            return done + "; marked done, it won't look for them again";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return done + $"; could not mark it done ({ex.Message}), so it runs again at the next start";
        }
    }

    // The files of folder when it holds a Skia or HarfBuzz library and nothing outside
    // OldBundleNatives (no sub-folder either): what a pre-3.186.9 bundle unpacked. Null
    // for anything else.
    private static FileInfo[]? OldNativesOnly(DirectoryInfo folder)
    {
        if (folder.EnumerateDirectories().Any()) return null;
        FileInfo[] files = folder.GetFiles();
        bool onlyOurs = files.All(f => OldBundleNatives.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
        bool renderer = files.Any(f => f.Name.Equals("libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals("libHarfBuzzSharp.dll", StringComparison.OrdinalIgnoreCase));
        return onlyOurs && renderer ? files : null;
    }

    // The .NET host names a bundle's folder with 12 characters of a base64url hash.
    private static bool LooksLikeBundleId(string name) =>
        name.Length == 12 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // True when path can be opened for writing with no sharing: nothing has it open or
    // mapped. A file that isn't there holds nothing.
    private static bool OpensExclusively(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Everything in folder, a link inside it removed as the link (never its target), and
    // MudPlay.dll last, so a clean-up cut short leaves a folder the next one still knows
    // for MudPlay's.
    private static void DeleteTree(DirectoryInfo folder)
    {
        foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) entry.Delete();
            else if (entry is DirectoryInfo sub) DeleteTree(sub);
            else if (entry.Name != MainAssembly) entry.Delete();
        }
        File.Delete(Path.Combine(folder.FullName, MainAssembly));
        folder.Delete();
    }

    private static bool AtOrUnder(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return SameFolder(p, f) || p.StartsWith(f + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
