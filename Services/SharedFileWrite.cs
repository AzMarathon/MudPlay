using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MudPlay.Services;

// How a store changes a file it shares with the other clients on this machine,
// when each client lays its own changes over what it reads there and writes the
// whole file back. Two clients doing that at the same instant each write over
// what the other has just written, and one client's change is gone. So the
// read-merge-write is done under a hold on a lock file beside the shared one
// (Hold), and the write goes to a temporary file moved over the real one
// (TempFor), so a reader sees the old file or the new, never half of one. A
// client that dies between the two leaves its temporary file behind, which the
// next to load the file removes (RemoveStaleTemps).
public static class SharedFileWrite
{
    // How long a client waits for another's hold before giving up for now. A
    // read-merge-write of these files takes milliseconds, and the wait is on the
    // UI thread (the heartbeat, a death, a Save), so it is kept short: a write that
    // can't have the hold is tried again on the next heartbeat.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(250);

    // The lock files whose last hold timed out. Until one is held again it is
    // tried once and not waited on: what keeps it from being held may be no other
    // client at all (a folder made read-only, a share that has stopped answering),
    // and a wait on every heartbeat would stall the UI thread that long each second.
    private static readonly ConcurrentDictionary<string, byte> Failing = new(StringComparer.Ordinal);

    private static string LockFor(string path) => path + ".lock";

    public static string TempFor(string path) => $"{path}.{Environment.ProcessId}.tmp";

    // Take the hold on path's lock file, waiting up to MaxWait for another
    // client's to end (not at all while it keeps failing, as Failing says). When
    // it doesn't end, the exception of the last try is thrown, for the caller to
    // treat as a write that failed. waited is how long the hold took to get, for
    // the caller's log.
    //
    // The lock file is opened to this client alone (FileShare.None): Windows
    // refuses any other open, and .NET on Linux and macOS takes an exclusive flock
    // that every client's Hold asks for too. Either is let go when the handle
    // closes or the process dies, so a crash leaves no hold behind. The file stays
    // between holds: deleting it at release would let a client still waiting on
    // the deleted file hold it while a third made and held a new one.
    public static IDisposable Hold(string path, out TimeSpan waited)
    {
        string lockPath = LockFor(path);
        TimeSpan wait = Failing.ContainsKey(lockPath) ? TimeSpan.Zero : MaxWait;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    FileStream hold = OpenAlone(lockPath);
                    waited = clock.Elapsed;
                    Failing.TryRemove(lockPath, out _);
                    return hold;
                }
                catch (IOException ex) when (ex is not DirectoryNotFoundException && clock.Elapsed < wait)
                {
                    Thread.Sleep(Math.Min(attempt, 10) * 2);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failing.TryAdd(lockPath, 0);
            throw;
        }
    }

    // Remove what a dead write of path left: the temporary files named as TempFor
    // names them. Done under the hold, which every write takes before it makes its
    // temporary file and keeps until the file is moved into place, so one seen then
    // is no write's in progress, whichever process (or machine sharing the folder)
    // made it. With the hold taken by another client just now, they are left for
    // the next load. Returns the files removed; one that can't be removed is left
    // too.
    public static IReadOnlyList<string> RemoveStaleTemps(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(folder) || name.Length == 0) return Array.Empty<string>();
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            List<string> temps = Directory.GetFiles(folder, name + ".*.tmp").Where(t => IsTempOf(t, name)).ToList();
            if (temps.Count == 0) return Array.Empty<string>();

            using FileStream hold = OpenAlone(LockFor(path));
            List<string> removed = new();
            foreach (string temp in temps)
            {
                try
                {
                    if (!File.Exists(temp)) continue;
                    File.Delete(temp);
                    removed.Add(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next load: a file that can't be removed now does
                    // no harm where it is.
                }
            }
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The hold is another client's, or the folder can't be read: the files
            // are left for the next load.
            return Array.Empty<string>();
        }
    }

    // "<name>.<process id>.tmp" and nothing looser: another writer's temporary
    // files are its own to clear.
    private static bool IsTempOf(string temp, string name)
    {
        string file = Path.GetFileName(temp);
        return file.Length > name.Length + 5
            && file.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
            && file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(file.AsSpan(name.Length + 1, file.Length - name.Length - 5), NumberStyles.None,
                CultureInfo.InvariantCulture, out _);
    }

    private static FileStream OpenAlone(string lockPath) =>
        new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
}
