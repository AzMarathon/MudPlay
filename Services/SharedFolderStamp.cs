using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace MudPlay.Services;

// SharedFileStamp for a folder tree. A store whose files are one per record, in
// sub-folders (the loops and lair setups of a game-data set), marks the tree as it
// read or wrote it and asks later whether another client has changed it.
//
// The mark is each matching file's write time and length plus the names of the
// folders, so an edit, a new file, a removed one, a move and an empty folder made
// elsewhere all show. A client's own write is noted by file, not by re-marking the
// tree, so a change another client made in the same moment isn't taken as ours.
//
// A change is reported only once the tree has looked the same on two polls running,
// so a burst of saves, or a move that is a delete and a write, is read once, settled.
public sealed class SharedFolderStamp
{
    public readonly record struct FileMark(long WriteTicks, long Length);

    public sealed class Snapshot
    {
        public Dictionary<string, FileMark> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

        public bool SameAs(Snapshot other) =>
            Files.Count == other.Files.Count
            && Folders.SetEquals(other.Folders)
            && Files.All(f => other.Files.TryGetValue(f.Key, out FileMark m) && m == f.Value);
    }

    private Snapshot _known = new();
    private Snapshot? _pending;

    // The tree as this client last read or wrote it.
    public Snapshot Known => _known;

    // Take the tree as it is now as the one this client holds. Called before a
    // full read, so a file written during the read shows on the next poll.
    public void Rebase(string root, string suffix)
    {
        _known = Scan(root, suffix);
        _pending = null;
    }

    // The tree as it now is when another client has changed it and it has held
    // still for two polls; null while it matches what this client holds, or is still
    // moving. settleNow skips the wait (a check before a change of the caller's own).
    public Snapshot? TakeSettledChange(string root, string suffix, bool settleNow = false)
    {
        Snapshot now = Scan(root, suffix);
        if (now.SameAs(_known))
        {
            _pending = null;
            return null;
        }
        if (!settleNow && (_pending is null || !_pending.SameAs(now)))
        {
            _pending = now;
            return null;
        }
        return now;
    }

    // The caller has read the tree as snapshot shows it.
    public void Accept(Snapshot snapshot)
    {
        _known = snapshot;
        _pending = null;
    }

    // This client wrote path (and made any folders above it).
    public void NoteWritten(string root, string path)
    {
        string full = Path.GetFullPath(path);
        _known.Files[full] = MarkOf(full);
        string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? dir = Path.GetDirectoryName(full);
             !string.IsNullOrEmpty(dir) && dir.Length > top.Length && dir.StartsWith(top, StringComparison.Ordinal);
             dir = Path.GetDirectoryName(dir))
            _known.Folders.Add(dir);
    }

    // This client removed path.
    public void NoteGone(string path) => _known.Files.Remove(Path.GetFullPath(path));

    // This client made or removed a folder that holds no file of the tree's.
    public void NoteFolders(string root, string suffix)
    {
        _known.Folders.Clear();
        _known.Folders.UnionWith(Scan(root, suffix).Folders);
    }

    // Files ending in suffix anywhere under root, and every folder under it.
    public static Snapshot Scan(string root, string suffix)
    {
        Snapshot snapshot = new();
        if (!Directory.Exists(root)) return snapshot;
        // Default attribute skipping, so the tree is the one Directory.EnumerateFiles
        // reads when a catalogue is loaded.
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        // Read straight off the directory entries: this runs on every heartbeat over a
        // tree of hundreds of files, so a file that doesn't match is never stat'ed and
        // none gets a FileInfo.
        FileSystemEnumerable<(string Path, bool IsFolder, FileMark Mark)> entries = new(
            Path.GetFullPath(root),
            (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory,
                e.IsDirectory ? default : new FileMark(e.LastWriteTimeUtc.UtcTicks, e.Length)),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                e.IsDirectory || e.FileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase),
        };
        try
        {
            foreach ((string path, bool isFolder, FileMark mark) in entries)
            {
                if (isFolder) snapshot.Folders.Add(path);
                else snapshot.Files[path] = mark;
            }
        }
        catch (IOException)
        {
            // A folder removed while the tree is read: what was seen stands, and the
            // next poll sees the rest.
        }
        return snapshot;
    }

    private static FileMark MarkOf(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? new FileMark(file.LastWriteTimeUtc.Ticks, file.Length) : default;
    }
}
