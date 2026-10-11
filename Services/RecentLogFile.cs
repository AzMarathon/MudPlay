using System.Text;
using System.Threading.Channels;

namespace MudPlay.Services;

// Keeps the last few minutes of the program log on disk, always, so a hang or a
// forced kill leaves something to send: no bug report can be made of a client
// that is no longer running, and the program log otherwise lives only in memory.
//
// Two files per client process, written in turn. Each takes entries for
// SegmentSpan (or until SegmentMaxBytes); then the other is emptied and takes
// over. What a reader finds after a kill is the full older file and the part-
// written newer one: between one and two spans, two to four minutes, of log.
// Each file opens with the same header (build, system, start time), so either
// one stands on its own. The pair can never pass 2 × SegmentMaxBytes.
//
// Entries are handed to a queue and written by a background task, flushed once
// per batch: the thread that logged (usually the UI thread) never touches the
// file, and a UI thread that has hung doesn't stop what it already logged from
// reaching the disk. A killed process loses at most the batch in flight.
//
// Not written while Auto-collect logs is on: the full program log is then on
// disk from whenever it was switched on, and is the record.
public sealed class RecentLogFile : IAsyncDisposable
{
    // Two of these are what survives: 2 to 4 minutes.
    public static readonly TimeSpan SegmentSpan = TimeSpan.FromMinutes(2);

    // A busy session logs about 8 entries of about 100 bytes a second: some
    // 100 KB a span. The cap leaves ten times that for a burst before a file
    // rolls early.
    public const int SegmentMaxBytes = 1024 * 1024;

    // How many client processes' files are kept when a new one starts: this one
    // and the two before it, enough to cover "it hung, I restarted, it hung
    // again".
    public const int ProcessesKept = 3;

    public const string TopicA = "recent-a";
    public const string TopicB = "recent-b";

    private readonly LogService _log;
    private readonly LogDiagnosticState _diagnostics;
    private readonly string[] _paths;
    private readonly Func<IReadOnlyList<string>> _header;
    private readonly Func<string, TextWriter> _open;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    // Touched only by the pump.
    private TextWriter? _writer;
    private volatile int _active = -1;
    private long _segmentBytes;
    private DateTimeOffset _segmentStart;
    private bool _broken;
    private volatile bool _paused;
    private volatile int _filesWritten;

    private readonly record struct Item(LogEntry? Entry, TaskCompletionSource? Barrier);

    // pathA / pathB: the two files. header: the lines each file opens with.
    // open: how a file is opened for writing from empty (a seam for tests).
    public RecentLogFile(
        LogService log,
        LogDiagnosticState diagnostics,
        string pathA,
        string pathB,
        Func<IReadOnlyList<string>> header,
        Func<string, TextWriter>? open = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        ArgumentException.ThrowIfNullOrEmpty(pathA);
        ArgumentException.ThrowIfNullOrEmpty(pathB);
        _paths = [pathA, pathB];
        _header = header ?? throw new ArgumentNullException(nameof(header));
        _open = open ?? OpenTruncated;
        _paused = diagnostics.AutoCollectLogs;
        _pump = Task.Run(PumpAsync);
        _log.EntryAdded += OnEntryAdded;
        _diagnostics.Changed += OnDiagnosticsChanged;
    }

    // The files this process has written so far, the one being written last.
    public IReadOnlyList<string> CurrentFiles
    {
        get
        {
            int written = _filesWritten;
            int active = _active;
            if (written == 0 || active < 0) return [];
            return written == 1 ? [_paths[active]] : [_paths[1 - active], _paths[active]];
        }
    }

    private static TextWriter OpenTruncated(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void OnDiagnosticsChanged()
    {
        _paused = _diagnostics.AutoCollectLogs;
        // Wake the pump so it closes the file now, not at the next entry (there
        // won't be one while paused).
        _queue.Writer.TryWrite(default);
    }

    private void OnEntryAdded(LogEntry entry) => Enqueue(entry);

    // On the producer's thread: nothing here but the hand-off.
    internal void Enqueue(LogEntry entry)
    {
        if (_paused) return;
        _queue.Writer.TryWrite(new Item(entry, null));
    }

    // Completes once everything queued before the call has been written and
    // flushed.
    internal Task WhenWrittenAsync()
    {
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Item(null, barrier))) barrier.TrySetResult();
        return barrier.Task;
    }

    private async Task PumpAsync()
    {
        ChannelReader<Item> reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            List<TaskCompletionSource>? barriers = null;
            while (reader.TryRead(out Item item))
            {
                if (item.Barrier is { } barrier) (barriers ??= new()).Add(barrier);
                else if (item.Entry is { } entry) Write(entry);
            }
            if (_paused) Close();
            else Flush();
            if (barriers is not null)
                foreach (TaskCompletionSource barrier in barriers) barrier.TrySetResult();
        }
        Close();
    }

    private void Write(LogEntry entry)
    {
        if (_broken || _paused) return;
        try
        {
            string line = $"{entry.Timestamp:HH:mm:ss.fff} [{entry.Severity}] {entry.Source}: {entry.Message}";
            int bytes = Encoding.UTF8.GetByteCount(line) + 1;
            bool roll = _writer is null
                || entry.Timestamp - _segmentStart >= SegmentSpan
                || _segmentBytes + bytes > SegmentMaxBytes;
            if (roll) Roll(entry.Timestamp);
            _writer!.Write(line);
            _writer.Write('\n');
            _segmentBytes += bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Disk full, folder gone, no permission. Logging it would come
            // straight back here; the file is given up for this session.
            _broken = true;
            Close();
        }
    }

    private void Roll(DateTimeOffset at)
    {
        Close();
        _active = _active == 0 ? 1 : 0;
        _writer = _open(_paths[_active]);
        _segmentStart = at;
        _segmentBytes = 0;
        if (_filesWritten < 2) _filesWritten++;
        foreach (string line in _header())
        {
            _writer.Write(line);
            _writer.Write('\n');
            _segmentBytes += Encoding.UTF8.GetByteCount(line) + 1;
        }
        string opened = $"# this file from {at:yyyy-MM-dd HH:mm:ss zzz}; the client's other last-minutes file holds the minutes before it";
        _writer.Write(opened);
        _writer.Write('\n');
        _segmentBytes += Encoding.UTF8.GetByteCount(opened) + 1;
    }

    private void Flush()
    {
        try { _writer?.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _broken = true;
            Close();
        }
    }

    private void Close()
    {
        TextWriter? writer = _writer;
        _writer = null;
        if (writer is null) return;
        try { writer.Dispose(); }
        catch (IOException) { /* a failed last flush loses its batch; nothing more to do with a closing file */ }
    }

    public async ValueTask DisposeAsync()
    {
        _log.EntryAdded -= OnEntryAdded;
        _diagnostics.Changed -= OnDiagnosticsChanged;
        _queue.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    // Delete the last-minutes files of all but the newest `keep` client processes
    // (a process's pair shares everything in its names but the a / b). Returns how
    // many files went. A file another running client still holds is left.
    public static int PruneOldProcesses(string directory, int keep = ProcessesKept)
    {
        if (!Directory.Exists(directory)) return 0;
        List<IGrouping<string, FileInfo>> processes = new DirectoryInfo(directory)
            .EnumerateFiles("*-recent-?.log", SearchOption.TopDirectoryOnly)
            .GroupBy(f => f.Name[..^"-a.log".Length], StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(f => f.LastWriteTimeUtc))
            .ToList();
        int deleted = 0;
        foreach (IGrouping<string, FileInfo> old in processes.Skip(Math.Max(0, keep)))
        {
            foreach (FileInfo file in old)
            {
                try { file.Delete(); deleted++; }
                catch (IOException) { /* held by a client that is still running */ }
                catch (UnauthorizedAccessException) { /* not ours to remove */ }
            }
        }
        return deleted;
    }
}
