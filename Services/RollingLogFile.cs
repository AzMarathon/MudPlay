namespace MudPlay.Services;

// A line-capped append log persisted to a single file. Retains only the last
// MaxLines lines on disk — the tail rolls forward as new lines arrive, so a
// long-lived log can't grow without bound. Content survives process restarts:
// Open reloads the existing tail and appends continue from there.
//
// Thread-safe behind a single lock. Append does only the in-memory bookkeeping
// (add + trim) on the caller's thread, then hands the actual file write to a
// coalesced BACKGROUND flush — the whole file is still rewritten so the on-disk
// count can't overshoot the cap, but that rewrite no longer runs on the caller's
// thread. The chat / transaction pump runs on the UI thread, and a full rewrite
// per line stalled the Conversation window under heavy chat; off-loading it keeps
// the window smooth. A single-flight writer serializes writes and always persists
// the LATEST tail, so a burst of appends collapses to one rewrite and no two
// writes race. Rare operations that must be immediately visible (Close, Truncate,
// SetMaxLines) wait for the write via Flush. I/O failures (a locked file, a
// permission flap) are swallowed — a log that can't be written must never take
// down the feature it's recording. Any other write failure is logged and still
// frees the writer. Flush runs on the UI thread (profile swap, Clear chatlog, the
// line-count picker), so it hands the write to the same background writer and
// waits at most _flushWait: a stuck disk or a dead writer must never freeze the
// client.
public sealed class RollingLogFile
{
    // Long enough for any healthy rewrite of a few thousand lines, short enough
    // that a stuck disk costs a pause rather than a hang.
    private static readonly TimeSpan DefaultFlushWait = TimeSpan.FromSeconds(3);

    private readonly LogService _log;
    private readonly Action<string, string[]> _write;
    private readonly TimeSpan _flushWait;
    private readonly object _gate = new();       // guards _lines / _path / the flush flags
    private readonly object _writeGate = new();   // serializes the actual file writes
    private readonly List<string> _lines = new();
    private string? _path;
    private int _maxLines = 1;

    // Single-flight flush state (guarded by _gate). _flushRunning is true while a
    // background flush is queued or running; _flushAgain records that more appends
    // landed during a write, so the writer loops once more with the newer tail.
    private bool _flushRunning;
    private bool _flushAgain;

    // Set (signalled) whenever no background flush is queued or running. Reset when a
    // flush is scheduled, set again when the writer finishes, however it ends. Flush()
    // waits on it, for at most FlushWait, so a caller that reads the file right after
    // Flush()/Close() can't race a background write that was still queued (the app
    // reads the in-memory Snapshot, so this only matters for a direct-file reader).
    private readonly System.Threading.ManualResetEventSlim _idle = new(initialState: true);

    public RollingLogFile(LogService log) : this(log, File.WriteAllLines, DefaultFlushWait) { }

    // The file write and the Flush bound are injectable so a failing or stuck disk
    // can be reproduced; the client always uses File.WriteAllLines and DefaultFlushWait.
    internal RollingLogFile(LogService log, Action<string, string[]> write, TimeSpan flushWait)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(write);
        _log = log;
        _write = write;
        _flushWait = flushWait;
    }

    public bool IsOpen
    {
        get { lock (_gate) { return _path is not null; } }
    }

    // A point-in-time copy of the retained tail, oldest line first — the same
    // lines an Open reloaded from disk. Lets a caller replay the persisted
    // history back into an in-memory store on reconnect without re-reading the
    // file itself. Always current (the in-memory tail is updated synchronously on
    // Append, even though the disk write is deferred).
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) { return _lines.ToArray(); }
    }

    // Point the log at path with the given cap, loading any existing tail so
    // appends continue across restarts. Re-opening at a different path drops the
    // previous file's in-memory tail first. maxLines <= 0 is clamped to 1.
    public void Open(string path, int maxLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            _path = path;
            _maxLines = Math.Max(1, maxLines);
            _lines.Clear();
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadLines(path)) _lines.Add(line);
                    TrimLocked();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Stop writing; the file keeps its content on disk for the next session. Flush
    // the pending tail first (synchronously) so a swap / disconnect never drops the
    // last appends that were still queued for the background writer.
    public void Close()
    {
        Flush();
        lock (_gate)
        {
            _path = null;
            _lines.Clear();
        }
    }

    // Adjust the cap live (the shared line-count picker changed). Trims + flushes
    // only when the new cap actually sheds rows. Synchronous — a settings change is
    // rare and the user expects the file trimmed at once.
    public void SetMaxLines(int maxLines)
    {
        bool trimmed;
        lock (_gate)
        {
            _maxLines = Math.Max(1, maxLines);
            trimmed = TrimLocked();
        }
        if (trimmed) Flush();
    }

    public void Append(string line)
    {
        bool schedule = false;
        lock (_gate)
        {
            if (_path is null) return;
            _lines.Add(line);
            TrimLocked();
            schedule = RequestFlushLocked();
        }
        if (schedule) Task.Run(BackgroundFlush);
    }

    // Coalesced single-flight: if a background flush is already in flight, just
    // mark that it must run again with the newer tail; otherwise claim the slot and
    // tell the caller to queue one. Only the false→true transition schedules work,
    // so there is never more than one background flush at a time.
    private bool RequestFlushLocked()
    {
        if (_flushRunning) { _flushAgain = true; return false; }
        _flushRunning = true;
        _idle.Reset();
        return true;
    }

    // Wipe the file and the in-memory tail. Driven by the user's explicit Clear
    // (Clear chatlog menu / Transaction-history Clear button), never by a session
    // boundary — the whole point of the log is to persist across those. Synchronous.
    public void Truncate()
    {
        lock (_gate) { _lines.Clear(); }
        Flush();
    }

    // Block until the current tail is on disk, or until _flushWait runs out. Appends
    // persist asynchronously (off the caller's thread); Flush requests a write of the
    // current tail and waits for it — used by Close / Truncate / SetMaxLines and
    // available to callers that need the file current now. The write itself runs on
    // the background writer, so the wait is the only thing that can hold the caller.
    public void Flush()
    {
        string path;
        bool schedule;
        lock (_gate)
        {
            if (_path is null) return;
            path = _path;
            schedule = RequestFlushLocked();
        }
        if (schedule) Task.Run(BackgroundFlush);
        if (_idle.Wait(_flushWait)) return;

        // The request stays queued and the next write replaces the whole file, so a
        // still-open file catches up to the current tail once the disk answers. A
        // Close carrying on drops what was not yet written.
        _log.Warn("SessionLog",
            $"Writing {Path.GetFileName(path)} did not finish within {_flushWait.TotalSeconds:0.#}s; " +
            "carrying on, recent lines may be unwritten");
    }

    // The background writer. Holds _writeGate for its whole run so it never races
    // another background flush; takes _gate only briefly to snapshot the tail and
    // check the re-run flag, so an Append or a Flush (which need only _gate) is
    // never blocked by the disk I/O itself. Loops while appends keep
    // arriving mid-write, then releases the single-flight slot. The normal exits
    // release under the same _gate hold that saw no re-run pending, so an Append
    // can't slip between the check and the release; the finally frees the slot on
    // any other way out, since a slot left claimed would stall every later Flush.
    private void BackgroundFlush()
    {
        bool released = false;
        try
        {
            lock (_writeGate)
            {
                while (true)
                {
                    string path;
                    string[] snapshot;
                    lock (_gate)
                    {
                        if (_path is null) { ReleaseFlushLocked(); released = true; return; }
                        path = _path;
                        snapshot = _lines.ToArray();
                        _flushAgain = false;
                    }
                    WriteToDisk(path, snapshot);
                    lock (_gate)
                    {
                        if (!_flushAgain) { ReleaseFlushLocked(); released = true; return; }
                    }
                }
            }
        }
        finally
        {
            if (!released) lock (_gate) { ReleaseFlushLocked(); }
        }
    }

    private void ReleaseFlushLocked()
    {
        _flushRunning = false;
        _flushAgain = false;
        _idle.Set();
    }

    // Every write replaces the whole file, so the next one carries whatever a failed
    // one missed.
    private void WriteToDisk(string path, string[] lines)
    {
        try
        {
            _write(path, lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex)
        {
            _log.Error("SessionLog",
                $"Writing {Path.GetFileName(path)} failed ({ex.GetType().Name}: {ex.Message}); " +
                "recent lines may be unwritten until the next write");
        }
    }

    private bool TrimLocked()
    {
        bool trimmed = false;
        while (_lines.Count > _maxLines)
        {
            _lines.RemoveAt(0);
            trimmed = true;
        }
        return trimmed;
    }
}
