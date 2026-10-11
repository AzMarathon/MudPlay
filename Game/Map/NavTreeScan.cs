using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using MudPlay.Services;
using static MudPlay.Services.SharedFolderStamp;

namespace MudPlay.Game.Map;

// One walk of a set's Loops tree: every file with one of the given suffixes (write
// time and length) and every folder. Immutable once made, so the walk can run on a
// worker thread and the result be handed to the UI thread. Loops and lair setups share
// the tree, so one walk serves both.
internal sealed class NavTreeScan
{
    private readonly Dictionary<string, Dictionary<string, FileMark>> _files;

    private NavTreeScan(string root, bool complete, Dictionary<string, Dictionary<string, FileMark>> files, HashSet<string> folders)
    {
        Complete = complete;
        _files = files;
        Folders = folders;
        BareFolders = BareOf(root, files, folders);
    }

    // False when the root is missing or the tree could not be walked to the end (a
    // share that is down, a folder that went while it was read). Its files are then
    // not "all the files there are", and nothing may be concluded from what is absent.
    public bool Complete { get; }

    public HashSet<string> Folders { get; }

    public IReadOnlyDictionary<string, FileMark> FilesWith(string suffix) => _files[suffix];

    // Folders with no loop or lair file at or below them. A folder that holds files
    // is covered by the files' own changes; one that holds none is only here. Worked
    // out when the walk is made, so the UI thread never does it.
    public HashSet<string> BareFolders { get; }

    private static HashSet<string> BareOf(string root, Dictionary<string, Dictionary<string, FileMark>> files, HashSet<string> folders)
    {
        HashSet<string> withFiles = new(StringComparer.Ordinal);
        foreach (string path in files.Values.SelectMany(f => f.Keys))
            for (string? dir = Path.GetDirectoryName(path);
                 !string.IsNullOrEmpty(dir) && dir.Length > root.Length && withFiles.Add(dir);
                 dir = Path.GetDirectoryName(dir)) { }
        return folders.Where(f => !withFiles.Contains(f)).ToHashSet(StringComparer.Ordinal);
    }

    public bool SameAs(NavTreeScan other) =>
        Folders.SetEquals(other.Folders)
        && _files.All(kv => other._files.TryGetValue(kv.Key, out Dictionary<string, FileMark>? theirs)
            && theirs.Count == kv.Value.Count
            && kv.Value.All(f => theirs.TryGetValue(f.Key, out FileMark m) && m == f.Value));

    // Walk root for files ending in any of suffixes. The files are the ones
    // Directory.EnumerateFiles(root, "*" + suffix, SearchOption.AllDirectories) finds
    // when a catalogue is loaded: hidden and dot-named entries included, and the
    // suffix matched with the platform's case rules.
    public static NavTreeScan Walk(string root, params string[] suffixes)
    {
        string full = Path.GetFullPath(root);
        Dictionary<string, Dictionary<string, FileMark>> files = suffixes.ToDictionary(s => s, _ => new Dictionary<string, FileMark>(StringComparer.Ordinal));
        HashSet<string> folders = new(StringComparer.Ordinal);
        if (!Directory.Exists(full)) return new NavTreeScan(full, false, files, folders);

        StringComparison casing = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };
        // Read straight off the directory entries: this runs over a tree of hundreds
        // of files, so a file that doesn't match is never stat'ed and none gets a
        // FileInfo.
        FileSystemEnumerable<(string Path, string? Suffix, FileMark Mark)> entries = new(
            full,
            (ref FileSystemEntry e) => e.IsDirectory
                ? (e.ToFullPath(), null, default)
                : (e.ToFullPath(), SuffixOf(e.FileName, suffixes, casing), new FileMark(e.LastWriteTimeUtc.UtcTicks, e.Length)),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || SuffixOf(e.FileName, suffixes, casing) is not null,
        };
        try
        {
            foreach ((string path, string? suffix, FileMark mark) in entries)
            {
                if (suffix is null) folders.Add(path);
                else files[suffix][path] = mark;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NavTreeScan(full, false, files, folders);
        }
        return new NavTreeScan(full, true, files, folders);
    }

    private static string? SuffixOf(ReadOnlySpan<char> name, string[] suffixes, StringComparison casing)
    {
        foreach (string suffix in suffixes)
            if (name.EndsWith(suffix, casing)) return suffix;
        return null;
    }
}
