namespace MudPlay.Services;

// Tees the in-memory program log (LogService) to an on-disk file so a hard
// lockup or a kill -9, where no clean crash handler ever runs, still leaves a
// post-mortem trail. LogService alone keeps only its ring buffer in memory, which
// evaporates the moment the process dies; the crash reporter only fires on a
// managed exception, not on a hang.
//
// The file lands under Logs/ as {timestamp}-p{pid}-program.log and is covered by
// the same DebugLogWriter.PruneOldLogs retention sweep as every other .log.
// One instance per app session, made in AppServices.
//
// Gated by LogDiagnosticState.AutoCollectLogs: off (the default) means no file is
// created at all; flipping the Log-pane "Auto-collect logs" toggle on starts a
// fresh file, off closes it. The trade-off is deliberate: a default-off collector
// keeps normal sessions from littering Logs, at the cost of the post-mortem trail
// unless the user opts in.
//
// Entries are handed to a queue (LogEntryQueue) and written in order by one
// background task, flushed once per batch. The thread that logged, often the UI
// thread, never touches the disk: a slow or locked file (antivirus, a sharing
// violation) can't freeze the window on a log line, and no thread waits behind
// another's stalled write. While the disk is stuck the queue keeps the newest
// QueueDepth entries and the file says how many went once writing resumes.
//
// A write that fails closes the file; the failure is noted once in the program
// log (kept out of this file, which is what failed), and the same file is reopened
// with the next entry logged after a retry delay. A later failure is noted again
// only once a write has succeeded in between, or a new file has been started.
//
// What is queued is written before the file closes: on Auto-collect going off
// (the switch never waits for it), at process exit (Dispose, bounded by
// ExitDrainBound) and before a crash report names the file (WaitWritten, bounded
// by CrashDrainBound).
public sealed class ProgramLogFile : IDisposable
{
    // Entries that may wait for the disk: the same depth, and reasoning, as the
    // last-minutes log (RecentLogFile.QueueDepth).
    public const int QueueDepth = RecentLogFile.QueueDepth;

    // How long the client's exit waits for queued entries to reach the file.
    public static readonly TimeSpan ExitDrainBound = TimeSpan.FromSeconds(3);

    // How long a crash report waits for them before it names the file.
    public static readonly TimeSpan CrashDrainBound = TimeSpan.FromSeconds(2);

    // The source of this class's own notices in the program log.
    public const string LogSource = "ProgramLog";

    private const string Topic = "program";

    private readonly LogService _log;
    private readonly LogDiagnosticState _diagnostics;
    private readonly Func<string, TextWriter> _openFile;
    private readonly TimeSpan _retryDelay;
    private readonly LogEntryQueue<Item> _queue = new(QueueDepth);
    private readonly CancellationTokenSource _stopping = new();

    // One stretch of Auto-collect being on, and the file it is written to.
    private sealed record Session(int Number, string Path, DateTimeOffset Started);

    // An entry, or (Entry null) the start of a session, so its file opens when
    // the switch goes on rather than with its first entry.
    private readonly record struct Item(LogEntry? Entry, Session Session);

    // Held for the switch and for each hand-off, never for any disk work: an
    // entry is queued under the session that was on when it was logged, so the
    // queue holds the sessions in order and an entry always lands in its own
    // session's file.
    private readonly object _switchGate = new();
    private int _sessions;

    // Set by the switch; read lock-free by CurrentPath.
    private volatile bool _wanted;
    private volatile Session? _latest;

    // The newest session whose file has been made; set by the pump, read by
    // CurrentPath. Kept while a failed file waits to be reopened: the file is
    // there, with everything up to the failure.
    private volatile Session? _made;

    // Touched only by the pump.
    private TextWriter? _writer;
    private Session? _writerSession;
    private bool _headerOnDisk;
    private bool _writingEntry;
    private int _unflushed;
    private int _dropped;
    private int _maybeLost;
    private string? _failedWith;
    private bool _resumeLinePending;
    private bool _failureNoted;

    // True on a thread while it is putting this class's own notice in the program
    // log: that entry must not come back here to the file whose failure it reports.
    [ThreadStatic] private static bool t_noting;

    // open: how the file is opened for appending. retryDelay: how long the pump
    // waits after a failed write before it opens the file again.
    public ProgramLogFile(
        LogService log,
        LogDiagnosticState diagnostics,
        Func<string, TextWriter>? open = null,
        TimeSpan? retryDelay = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _openFile = open ?? OpenAppending;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        // Not awaited by anyone: the pump catches whatever its writes throw and
        // ends only when the queue is completed.
        _ = Task.Run(PumpAsync);
        _log.EntryAdded += OnEntryAdded;
        _diagnostics.Changed += OnDiagnosticsChanged;
        SyncSwitch();
    }

    // The file being written, or null while Auto-collect logs is off (or before
    // the file of a session just switched on has been made). Never waits.
    public string? CurrentPath =>
        _wanted && _made is { } made && ReferenceEquals(made, _latest) ? made.Path : null;

    private static TextWriter OpenAppending(string path)
    {
        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream);
    }

    private void OnDiagnosticsChanged() => SyncSwitch();

    // Follow AutoCollectLogs. Each switch-on starts a new session (a new file);
    // the file itself is opened by the pump, so the switch never touches the disk.
    private void SyncSwitch()
    {
        lock (_switchGate)
        {
            bool want = _diagnostics.AutoCollectLogs;
            if (want == _wanted) return;
            _wanted = want;
            if (!want) return;
            Session session = new(++_sessions, AppPaths.NewDebugLogFile(Topic), DateTimeOffset.Now);
            _latest = session;
            _queue.Post(new Item(null, session));
        }
        // Switched off: wake the pump so it closes the file now.
        _queue.Wake();
    }

    // On the producer's thread (the UI thread, the Telnet read loop, a timer):
    // nothing here but the hand-off, which never waits on the disk.
    private void OnEntryAdded(LogEntry entry)
    {
        if (t_noting) return;
        lock (_switchGate)
        {
            if (_wanted && _latest is { } session) _queue.Post(new Item(entry, session));
        }
    }

    // Completes once everything logged before the call has been written and
    // flushed (or let go of: see QueueDepth).
    internal Task WhenWrittenAsync() => _queue.WhenWrittenAsync();

    // The crash path: wait at most `bound` for what is queued to reach the file.
    // True when it did.
    public bool WaitWritten(TimeSpan bound) => _queue.WaitWritten(bound);

    private async Task PumpAsync()
    {
        while (await _queue.WaitToReadAsync().ConfigureAwait(false))
        {
            Exception? failure = null;
            try
            {
                while (_queue.TryRead(out Item item)) Write(item);
                Flush();
                // Switched off: everything logged before it is written above. One
                // that slipped in after the last read reopens its own file next.
                if (!_wanted) Close();
            }
            catch (Exception ex)
            {
                // A disk error, or anything a writer throws: the pump must outlive
                // it, or the queue would fill behind a task that has ended.
                failure = ex;
            }
            if (failure is null)
            {
                _queue.EndBatch();
                continue;
            }

            _maybeLost += _unflushed + (_writingEntry ? 1 : 0);
            _unflushed = 0;
            _writingEntry = false;
            _failedWith = $"{failure.GetType().Name}: {failure.Message}";
            _resumeLinePending = true;
            Close();
            NoteOnce($"the program log file could not be written ({_failedWith}); "
                + $"it is opened again with the next entry logged after {_retryDelay.TotalSeconds:0.#} s, and this is the only notice until a write succeeds");
            // After the notice: whoever waited on this batch finds it in the log.
            _queue.EndBatch();
            try { await Task.Delay(_retryDelay, _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* the client is closing: what is left gets its one last try now */ }
        }
        Close();
    }

    private void Write(Item item)
    {
        // Until it is in the writer, a failure loses this entry too.
        _writingEntry = item.Entry is not null;
        if (_writer is null || !ReferenceEquals(item.Session, _writerSession)) OpenSession(item.Session);
        if (item.Entry is not { } entry) return;
        _dropped += _queue.TakeDropped();
        if (_dropped > 0)
        {
            WriteLine($"# {(_dropped == 1 ? "1 entry" : $"{_dropped} entries")} dropped while the disk was busy");
            _dropped = 0;
        }
        WriteLine($"{entry.Timestamp:HH:mm:ss.fff} [{entry.Severity}] {entry.Source}: {entry.Message}");
        _unflushed++;
        _writingEntry = false;
    }

    // Close whatever is open and open the session's file. A new file gets its
    // header (again, if a failed flush lost it); a file reopened after a failure
    // says so before anything else goes in.
    private void OpenSession(Session session)
    {
        bool reopening = ReferenceEquals(session, _writerSession);
        // Flushed here, where a failure is seen, rather than in Close, which has
        // to swallow one.
        _writer?.Flush();
        _unflushed = 0;
        Close();
        if (!reopening)
        {
            // A new file: an earlier session's failure is not this one's, and a
            // failure here gets a notice of its own. The session is taken on
            // before the open, so an open that fails is retried as a reopen and
            // stays latched.
            _writerSession = session;
            _headerOnDisk = false;
            _failedWith = null;
            _resumeLinePending = false;
            _maybeLost = 0;
            _failureNoted = false;
        }
        _writer = _openFile(session.Path);
        _made = session;
        if (!_headerOnDisk)
        {
            WriteLine($"# MudPlay debug log — topic={Topic}");
            WriteLine($"# started {session.Started:O}");
        }
        if (_resumeLinePending && _failedWith is { } failure)
        {
            string lost = _maybeLost == 0 ? "" : $": up to {(_maybeLost == 1 ? "1 entry" : $"{_maybeLost} entries")} before this line may be missing";
            WriteLine($"# writing failed ({failure}) and starts again here{lost}");
            _resumeLinePending = false;
        }
    }

    // The platform's line ending, as the file has always had.
    private void WriteLine(string line) => _writer!.WriteLine(line);

    // The batch is on disk: a failure noted before it is over, and the next one
    // gets a notice of its own.
    private void Flush()
    {
        if (_writer is null) return;
        _writer.Flush();
        _unflushed = 0;
        _headerOnDisk = true;
        if (_failedWith is null) return;
        _failedWith = null;
        _maybeLost = 0;
        if (_failureNoted) Note(LogSeverity.Info, "the program log file is written again");
        _failureNoted = false;
    }

    private void Close()
    {
        TextWriter? writer = _writer;
        _writer = null;
        // _writerSession is kept: a reopen of the same session is told apart
        // from a new one by it.
        if (writer is null) return;
        try { writer.Dispose(); }
        catch (Exception) { /* only a writer a failure already broke gets here unflushed, its loss counted in _maybeLost; nothing more to do with a closing file */ }
    }

    private void NoteOnce(string message)
    {
        if (_failureNoted) return;
        _failureNoted = true;
        Note(LogSeverity.Warn, message);
    }

    // The entry is kept out of this file (t_noting): written here it would fail
    // the same way and report itself again.
    private void Note(LogSeverity severity, string message)
    {
        t_noting = true;
        try { _log.Log(severity, LogSource, message); }
        catch (Exception) { /* a reader of the log threw; the entry is in the ring, and the pump must go on */ }
        finally { t_noting = false; }
    }

    // Stop taking entries and give what is queued at most ExitDrainBound to reach
    // the file. Run at process exit; a disk that is stuck can't hold the exit
    // longer than that. Running out of time is not reported: the process is
    // ending, and nothing it logs now would reach anyone.
    public void Dispose()
    {
        _log.EntryAdded -= OnEntryAdded;
        _diagnostics.Changed -= OnDiagnosticsChanged;
        // A pump waiting to retry stops waiting: what is queued gets its one
        // last try now.
        _stopping.Cancel();
        _queue.WaitWritten(ExitDrainBound);
        _queue.Complete();
    }
}
