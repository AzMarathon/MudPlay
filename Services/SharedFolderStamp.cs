using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services;

// SharedFileStamp for the files of a folder tree. A catalogue whose records are one
// file each, in sub-folders (the loops and the lair setups of a game-data set), holds
// the write time and length of every file as this client last read or wrote it, and
// asks later whether the tree now says something else.
//
// A client's own write is noted by file, not by re-marking the tree, so a change
// another client made in the same moment isn't taken as ours. Every note, and every
// full read, bumps Generation: a walk of the tree that began before one is stale, and
// whoever holds its result drops it.
public sealed class SharedFolderStamp
{
    public readonly record struct FileMark(long WriteTicks, long Length);

    private Dictionary<string, FileMark> _known = new(StringComparer.Ordinal);

    // The files as this client last read or wrote them.
    public IReadOnlyDictionary<string, FileMark> Known => _known;

    public long Generation { get; private set; }

    // The tree now holds these files: this client has read them all.
    public void Accept(IReadOnlyDictionary<string, FileMark> files)
    {
        _known = new Dictionary<string, FileMark>(files, StringComparer.Ordinal);
        Generation++;
    }

    // This client wrote path.
    public void NoteWritten(string path)
    {
        string full = Path.GetFullPath(path);
        FileInfo file = new(full);
        _known[full] = file.Exists ? new FileMark(file.LastWriteTimeUtc.Ticks, file.Length) : default;
        Generation++;
    }

    // This client removed path.
    public void NoteGone(string path)
    {
        _known.Remove(Path.GetFullPath(path));
        Generation++;
    }

    // True when files is not what this client holds.
    public bool Differs(IReadOnlyDictionary<string, FileMark> files) =>
        files.Count != _known.Count
        || files.Any(f => !_known.TryGetValue(f.Key, out FileMark m) || m != f.Value);
}
