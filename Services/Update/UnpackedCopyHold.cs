using System;
using System.Collections.Generic;
using System.IO;

namespace MudPlay.Services.Update;

// Every file of the unpacked copy a Windows client runs from (BundleExtraction),
// held open for the client's life.
//
// The copy sits in the temp folder, and a temp-file clean-up (Storage Sense, Disk
// Cleanup, a cleaner tool) deletes whatever no program has open. An assembly the
// client hasn't loaded yet is such a file, so a client left running overnight could
// find one gone the first time it needed it and crash on a click. A handle that
// doesn't share delete keeps each file there; sharing read keeps every client of
// the version, and the runtime, loading them. Another version's clean-up
// (BundleExtraction.CleanOld) sees the held files in use and leaves the folder.
public sealed class UnpackedCopyHold : IDisposable
{
    private readonly List<FileStream> _held = new();

    public string Folder { get; }
    public int Held => _held.Count;
    // Files that couldn't be opened: already gone, or held by something else.
    public int NotHeld { get; }

    private UnpackedCopyHold(string folder, int notHeld)
    {
        Folder = folder;
        NotHeld = notHeld;
    }

    public static UnpackedCopyHold Take(string folder)
    {
        List<FileStream> held = new();
        int notHeld = 0;
        IEnumerable<string> files;
        // A folder we can't list must not stop startup; nothing is held.
        try { files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
        foreach (string path in files)
        {
            try { held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notHeld++; }
        }
        UnpackedCopyHold hold = new(folder, notHeld);
        hold._held.AddRange(held);
        return hold;
    }

    // The startup log line and the bug report's.
    public string Describe() => NotHeld == 0
        ? $"{Held} unpacked file(s) held open against temp-file clean-up"
        : $"{Held} unpacked file(s) held open against temp-file clean-up, {NotHeld} could not be";

    public void Dispose()
    {
        foreach (FileStream f in _held) f.Dispose();
        _held.Clear();
    }
}
