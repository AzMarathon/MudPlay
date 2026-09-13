using System;
using System.Runtime.InteropServices;

namespace MudPlay.Services.Update;

// Pure platform facts the updater needs: which release asset matches THIS build,
// and whether we're a self-contained install that can actually be replaced (vs a
// `dotnet run` dev build, where self-update is a no-op). No I/O beyond reading the
// process path — kept pure so the asset-matching logic is unit-testable.
public static class UpdatePlatform
{
    // The RID segment that names this platform's release asset:
    // linux-x64 / win-x64 / osx-x64 / osx-arm64. Null when the running OS/arch has
    // no published build (e.g. linux-arm64) — the caller then reports
    // NoAssetForPlatform rather than downloading the wrong archive.
    public static string? CurrentRid()
    {
        string? os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"   :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx"   : null;
        if (os is null) return null;

        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null) return null;

        // Published matrix: linux-x64, win-x64, osx-x64, osx-arm64. linux/win only
        // ship x64; osx ships both. Anything outside that has no asset.
        return (os, arch) switch
        {
            ("linux", "x64")  => "linux-x64",
            ("win",   "x64")  => "win-x64",
            ("osx",   "x64")  => "osx-x64",
            ("osx",   "arm64")=> "osx-arm64",
            _ => null,
        };
    }

    // Windows ships a .zip; linux/macOS ship .tar.gz.
    public static string ArchiveExtension(string rid) =>
        rid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz";

    // The tail every asset for this platform ends with, e.g. "-linux-x64.tar.gz".
    // Null when this platform has no published build.
    public static string? AssetSuffix()
    {
        string? rid = CurrentRid();
        return rid is null ? null : $"-{rid}{ArchiveExtension(rid)}";
    }

    // True when name is the release asset for THIS platform — a MudPlay archive
    // whose tail matches AssetSuffix(). Case-insensitive on the extension only; the
    // publisher's naming is otherwise exact.
    public static bool MatchesCurrentPlatform(string? assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return false;
        if (AssetSuffix() is not { } suffix) return false;
        return assetName.StartsWith("MudPlay", StringComparison.OrdinalIgnoreCase)
            && assetName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    // The directory the running executable lives in — the install root a self-update
    // replaces. Null when the process path is unavailable.
    public static string? InstallDirectory()
    {
        string? exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? null : System.IO.Path.GetDirectoryName(exe);
    }

    // Whether this is a replaceable self-contained install rather than a dev run.
    // A `dotnet run` launches through the "dotnet" muxer or an apphost sitting under
    // a bin/Debug|Release tree — replacing either would be wrong (and pointless). We
    // require: a real apphost process path (not the dotnet muxer) whose install dir
    // isn't a build-output folder. Conservative on purpose — a false negative just
    // disables self-update (the user still sees the notice + can update by hand), a
    // false positive could clobber a working tree.
    public static bool IsSelfContainedInstall()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        string file = System.IO.Path.GetFileNameWithoutExtension(exe);
        if (file.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        string? dir = System.IO.Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return false;

        string norm = dir.Replace('\\', '/');
        if (norm.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
