using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// The files from earlier MudPlay versions that this version removes from beside its
// executable at startup: the one place the client says which files in its folder
// are leftovers of its own.
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

    // True when name, a file beside the executable exeName, is one of ours to remove.
    public static bool IsRetired(string name, string exeName) =>
        RetiredNames.Contains(name, StringComparer.Ordinal) || IsReplacedExecutable(name, exeName);

    // The Windows swap renames the replaced executable aside as "<exe>.old-<digits>"
    // because a client may still be running it.
    private static bool IsReplacedExecutable(string name, string exeName)
    {
        string prefix = exeName + ".old-";
        return name.Length > prefix.Length
               && name.StartsWith(prefix, StringComparison.Ordinal)
               && name[prefix.Length..].All(char.IsAsciiDigit);
    }

    // Delete the retired files beside the executable. One still in use (another
    // client running a renamed-aside copy) stays for a later startup. Returns a line
    // per file deleted or left, for the program log.
    public static IReadOnlyList<string> Clean(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        var lines = new List<string>();
        if (string.IsNullOrEmpty(exePath) || Path.GetDirectoryName(exePath) is not { Length: > 0 } dir) return lines;
        string exeName = Path.GetFileName(exePath);
        string[] files;
        // A folder we can't list must not stop startup.
        try { files = Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return lines; }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            if (!IsRetired(name, exeName)) continue;
            try
            {
                File.Delete(path);
                lines.Add($"removed {name} from {dir} (left by an earlier version)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"left {name} in {dir} for now: {ex.Message}");
            }
        }
        return lines;
    }
}
