using System.IO;

namespace MudPlay.Services;

// Tells a store when a file it shares with the other clients on this machine was
// written by one of them. Clients on one realm each hold a copy of the realm's files
// and used to write the whole file from that copy, so the last to save threw away
// what the others had recorded. A store marks the file each time it reads or writes
// it, and re-reads it when the mark no longer matches, on a poll and before a change
// of its own: a change is then made to what the last character recorded, not to a
// stale copy.
public sealed class SharedFileStamp
{
    private DateTime _seen = DateTime.MinValue;

    // Note the file as this client just read or wrote it.
    public void Mark(string? path) => _seen = WriteTime(path);

    // True when the file was written (made, or removed) since the last Mark.
    public bool ChangedOutside(string? path) => WriteTime(path) != _seen;

    private static DateTime WriteTime(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
