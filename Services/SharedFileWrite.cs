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

    // A temporary file this old is a dead write, whatever process id it carries:
    // an id is given to another process in time.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private static string LockFor(string path) => path + ".lock";

    public static string TempFor(string path) => $"{path}.{Environment.ProcessId}.tmp";

    // Take the hold on path's lock file, waiting up to MaxWait for another
    // client's to end. When it doesn't end, the IOException of the last try is
    // thrown, for the caller to treat as a write that failed. waited is how long
    // the hold took to get, for the caller's log.
    //
    // The lock file is opened to this client alone (FileShare.None): Windows
    // refuses any other open, and .NET on Linux and macOS takes an exclusive flock
    // that every client's Hold asks for too. Either is let go when the handle
    // closes or the process dies, so a crash leaves no hold behind. The file stays
    // between holds: deleting it at release would let a client still waiting on
    // the deleted file hold it while a third made and held a new one.
    public static IDisposable Hold(string path, out TimeSpan waited)
    {
        Stopwatch clock = Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                FileStream hold = new(LockFor(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1);
                waited = clock.Elapsed;
                return hold;
            }
            catch (IOException ex) when (ex is not DirectoryNotFoundException && clock.Elapsed < MaxWait)
            {
                Thread.Sleep(Math.Min(attempt, 10) * 2);
            }
        }
    }

    // Remove what a dead write of path left: the temporary files named as TempFor
    // names them whose process is gone, or that are older than StaleAfter. One a
    // live client may be writing now is left. Returns the files removed; one that
    // can't be removed is left for the next load.
    public static IReadOnlyList<string> RemoveStaleTemps(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(folder) || name.Length == 0) return Array.Empty<string>();
        string[] temps;
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            temps = Directory.GetFiles(folder, name + ".*.tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        List<string> removed = new();
        foreach (string temp in temps)
        {
            string file = Path.GetFileName(temp);
            // "<name>.<process id>.tmp" and nothing looser: another writer's
            // temporary files are its own to clear.
            if (file.Length <= name.Length + 5 || !file.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(file.AsSpan(name.Length + 1, file.Length - name.Length - 5), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int process))
                continue;
            try
            {
                if (Running(process) && DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) < StaleAfter) continue;
                File.Delete(temp);
                removed.Add(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next load: a file that can't be removed now does no
                // harm where it is.
            }
        }
        return removed;
    }

    private static bool Running(int process)
    {
        try
        {
            using Process found = Process.GetProcessById(process);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
