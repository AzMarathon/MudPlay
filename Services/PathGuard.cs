namespace MudPlay.Services;

// The one place that says whether a name may become part of a path.
//
// A BBS, a realm, a character profile and a game-data set are each a folder named
// by the user, and those names also arrive from global.json, a bbs.json, the
// command line and other folders' listings. Joined onto a path unchecked, ".."
// names the folder above, "a/b" a folder two levels down, and an absolute path
// replaces everything before it; several of the operations that take such a name
// then delete or move that folder with all it holds.
//
// Two rules, and every path built from a name goes through one of them.
//
//   A name being GIVEN to something (a new profile, a rename, a copy, a set to
//   import into) must be plain (IsPlainName): one file name that means the same on
//   every platform. The naming dialogs ask WhyNotPlain, so the user is told as they
//   type exactly what the stores would refuse.
//
//   A name being USED (load, save, delete, rename from) must be plain, or already be
//   there as a folder directly inside the one it belongs in (Usable). That lets a
//   profile made before this rule, under a name it now refuses (a colon or a
//   backslash on Linux, a trailing dot), still be loaded, renamed and deleted.
//
// Under both sits Child: whatever the name, the path it makes must resolve to
// something directly inside the folder it is meant for. AppPaths builds every such
// path through it, so a caller that checked nothing still cannot be sent elsewhere.
public static class PathGuard
{
    // Raised with each refusal, as a line for the program log (AppServices writes it
    // at Warn). A line already raised is not raised again while it is remembered: a
    // refused name is asked about again on every listing and every save.
    public static event Action<string>? Refused;

    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);
    private static readonly Queue<string> SaidInOrder = new();
    private const int SaidAtMost = 200;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Names Windows gives to devices, with or without an extension. Refused
    // everywhere, so a profile named on Linux can be carried to Windows.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // True when name is fit to be given to a new folder or file.
    public static bool IsPlainName(string? name) => WhyNotPlain(name) is null;

    // Why name can't be given, as a sentence for the person typing it, or null when
    // it can. what is the thing being named ("name", "BBS name").
    public static string? WhyNotPlain(string? name, string what = "name")
    {
        if (string.IsNullOrWhiteSpace(name)) return $"A {what} is needed.";
        if (name is "." or "..") return $"“{name}” can't be used as a {what}.";
        foreach (char c in name)
        {
            if (char.IsControl(c)) return $"A {what} can't contain control characters.";
            if (c is '/' or '\\' or ':' || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                return $"A {what} can't contain '{c}'.";
        }
        if (name[^1] is '.' or ' ') return $"A {what} can't end with a dot or a space.";
        int dot = name.IndexOf('.');
        if (DeviceNames.Contains(dot < 0 ? name : name[..dot]))
            return $"“{name}” is reserved by Windows and can't be used as a {what}.";
        return null;
    }

    // The full path of name as an entry directly inside parent, or null when it
    // would not be one: blank, "." or "..", or anything that resolves to another
    // folder, a deeper one, a drive or a device. Says nothing of whether it exists,
    // nor of whether the name is plain.
    public static string? Child(string parent, string? name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('\0')) return null;
        string inside, full;
        try
        {
            inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            full = Path.GetFullPath(Path.Combine(inside, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        // Directly inside, and under the very name asked for: on Windows a trailing
        // dot or space is dropped on the way, and would name a different folder.
        return string.Equals(Path.GetDirectoryName(full), inside, PathComparison)
               && string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal)
            ? full
            : null;
    }

    // True when name may be used for a folder of parent: a plain name, or one
    // already there as a folder directly inside it.
    public static bool Usable(string parent, string? name)
    {
        if (Child(parent, name) is not { } path) return false;
        return IsPlainName(name) || Directory.Exists(path);
    }

    // True when path lies strictly below folder, both taken as full paths.
    public static bool IsInside(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        string below = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > below.Length && full.StartsWith(below, PathComparison);
    }

    // Remove folder and all it holds, only when it lies strictly below within: never
    // within itself, nor anything beside or above it. False when it was refused;
    // true when the folder is gone, or was not there.
    public static bool DeleteTree(string folder, string within, string operation)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(within);
        if (!IsInside(folder, within))
        {
            Refuse(operation, "folder", folder);
            return false;
        }
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        return true;
    }

    // Say that operation was refused for name, and that nothing was changed.
    public static void Refuse(string operation, string what, string? name)
    {
        string line = $"{operation} refused: {Show(name)} can't be used as a {what}; nothing on disk was changed.";
        lock (Said)
        {
            if (!Said.Add(line)) return;
            // Kept from growing without end by forgetting the oldest, never by
            // going quiet: a refusal said twice is better than one not said.
            SaidInOrder.Enqueue(line);
            if (SaidInOrder.Count > SaidAtMost) Said.Remove(SaidInOrder.Dequeue());
        }
        Refused?.Invoke(line);
    }

    // A name as it can be read in a log: quoted, control characters spelled out.
    public static string Show(string? name) => name is null
        ? "(no name)"
        : "'" + string.Concat(name.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString())) + "'";
}
