using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// The files from earlier MudPlay versions and earlier updates that this version
// removes from beside its executable: the one place the client says which files in
// its folder are leftovers of its own.
//
// It lives in code, not in a list shipped beside the program, because the folder
// MudPlay runs from may be Downloads or the Desktop, and the swap helper never
// removes anything (an earlier one deleted a whole Downloads folder). The build
// that is running knows what it no longer needs; a file on disk could be stale,
// edited or missing. So every entry is an exact name, or a strict pattern tied to
// the executable's own name, directly beside the executable — never a wildcard that
// could match a file of the user's. A release that stops shipping a file adds that
// exact name to RetiredNames.
public static class RetiredFiles
{
    // Exact names of files a release used to install beside the executable and no
    // longer does. Empty while every release is the single executable.
    private static readonly string[] RetiredNames = [];

    // True when name, a file beside the executable exeName, is one of ours to remove
    // at startup. windows says whether this is a Windows install. "<exe>.old-<digits>"
    // is the copy the Windows swap renames the replaced executable aside to, because
    // a client may still be running it; only Windows makes one, so elsewhere a file
    // of that name is the user's.
    public static bool IsRetired(string name, string exeName, bool windows) =>
        RetiredNames.Contains(name, StringComparer.Ordinal)
        || (windows && HasDigitsAfter(name, exeName + ".old-"));

    // True when name is "<exe>.mudplay-update-<digits>": the temporary copy a swap
    // helper makes of the new executable, left behind when the helper was killed
    // before renaming it into place.
    public static bool IsAbandonedSwapCopy(string name, string exeName) =>
        HasDigitsAfter(name, exeName + ".mudplay-update-");

    private static bool HasDigitsAfter(string name, string prefix) =>
        name.Length > prefix.Length
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && name[prefix.Length..].All(char.IsAsciiDigit);

    // At startup: delete the retired files beside the executable. One still in use
    // (another client running a renamed-aside copy) stays for a later startup.
    // Returns a line per file deleted or left, for the program log. windows defaults
    // to this platform.
    public static IReadOnlyList<string> Clean(string? exePath = null, bool? windows = null)
    {
        bool onWindows = windows ?? OperatingSystem.IsWindows();
        return Delete(exePath, (name, exeName) => IsRetired(name, exeName, onWindows));
    }

    // Before an update starts its helper: delete the temporary copies an earlier,
    // killed helper left. Not at startup, where a client opened mid-update would
    // take the copy a running helper is about to rename into place.
    public static IReadOnlyList<string> CleanAbandonedSwapCopies(string? exePath = null) =>
        Delete(exePath, IsAbandonedSwapCopy);

    private static List<string> Delete(string? exePath, Func<string, string, bool> ours)
    {
        exePath ??= Environment.ProcessPath;
        var lines = new List<string>();
        if (string.IsNullOrEmpty(exePath) || Path.GetDirectoryName(exePath) is not { Length: > 0 } dir) return lines;
        // With no executable beside them, a renamed-aside copy may be the only
        // program left (a swap that failed to put the old one back), so nothing goes.
        if (!File.Exists(exePath)) return lines;
        string exeName = Path.GetFileName(exePath);
        string[] files;
        // A folder we can't list must not stop startup or an update.
        try { files = Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return lines; }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            if (!ours(name, exeName)) continue;
            try
            {
                File.Delete(path);
                lines.Add($"removed {name} from {dir} (left by an earlier version or update)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"left {name} in {dir} for now: {ex.Message}");
            }
        }
        return lines;
    }
}
