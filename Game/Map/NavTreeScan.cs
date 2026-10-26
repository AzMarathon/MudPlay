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

        // Read straight off the directory entries: this runs over a tree of hundreds
        // of files, so a file that doesn't match is never stat'ed and none gets a
        // FileInfo.
        FileSystemEnumerable<(string Path, string? Suffix, FileMark Mark)> entries = new(
            full,
            (ref FileSystemEntry e) => e.IsDirectory
                ? (e.ToFullPath(), null, default)
                : (e.ToFullPath(), SuffixOf(e.FileName, suffixes), new FileMark(e.LastWriteTimeUtc.UtcTicks, e.Length)),
            Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || SuffixOf(e.FileName, suffixes) is not null,
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

    // One number for everything a Walk of root would list: each folder's path and
    // each file's path, write time and length. Two signatures differ whenever two
    // walks would, bar a 64-bit collision, so a tree whose signature is unchanged
    // need not be walked. Made with no string per entry: it is the cheap check the
    // watcher runs every minute in every client. Null when the tree can't be walked
    // to the end (the root missing, a folder that can't be opened), which Walk
    // reports as an incomplete scan.
    public static long? Signature(string root, params string[] suffixes)
    {
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) return null;
        FileSystemEnumerable<ulong> entries = new(
            full,
            (ref FileSystemEntry e) => e.IsDirectory
                ? Mix((uint)string.GetHashCode(e.Directory), (uint)string.GetHashCode(e.FileName), 0, -1)
                : Mix((uint)string.GetHashCode(e.Directory), (uint)string.GetHashCode(e.FileName),
                    e.LastWriteTimeUtc.UtcTicks, e.Length),
            Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || SuffixOf(e.FileName, suffixes) is not null,
        };
        ulong sum = 0, count = 0;
        try
        {
            // A sum, so the order the file system lists entries in doesn't matter.
            foreach (ulong entry in entries)
            {
                sum += entry;
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return (long)(sum ^ SplitMix(count));
    }

    private static ulong Mix(uint folder, uint name, long ticks, long length) =>
        SplitMix(((ulong)folder << 32 | name) ^ SplitMix((ulong)ticks ^ SplitMix((ulong)length)));

    private static ulong SplitMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    // A sub-folder that can't be opened must fail the walk, not be skipped: skipped,
    // its loops would read as deleted by another client.
    private static readonly EnumerationOptions Options = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };

    private static readonly StringComparison Casing = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? SuffixOf(ReadOnlySpan<char> name, string[] suffixes)
    {
        foreach (string suffix in suffixes)
            if (name.EndsWith(suffix, Casing)) return suffix;
        return null;
    }
}
