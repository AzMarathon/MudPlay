using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The full program log on disk (Auto-collect logs): the thread that logs never
// waits for the disk, entries land in order, a stuck disk drops the oldest and
// the file says so, a failed write is noted once and retried, exit and a crash
// wait a bounded time for the queue, and the switch opens and closes files at
// runtime. Driven through LogService; the disk is the open seam.
public sealed class ProgramLogFileTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // Every file the log opened, in order, and where it was asked to go.
    private sealed class Disks
    {
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentQueue<TestDisk> Opened { get; } = new();
        public Func<TestDisk> Make { get; init; } = () => new TestDisk();

        public TextWriter Open(string path)
        {
            Paths.Enqueue(path);
            TestDisk disk = Make();
            Opened.Enqueue(disk);
            return disk;
        }

        public string AllText => string.Concat(Opened.Select(d => d.Text));
    }

    private static ProgramLogFile Make(LogService log, Disks disks, bool on = true,
        LogDiagnosticState? diagnostics = null, TimeSpan? retryDelay = null) =>
        new(log, diagnostics ?? new LogDiagnosticState { AutoCollectLogs = on }, disks.Open, retryDelay);

    [Fact]
    public async Task LoggingNeverWaitsForAStuckDisk_AndEntriesLandInOrderOnceItAnswers()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        Disks disks = new() { Make = () => disk };
        using ProgramLogFile file = Make(log, disks);
        try
        {
            // The pump opens the file on the switch and sticks writing its header.
            Assert.True(disk.Stuck.Wait(Patience));

            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) log.Info("Walker", $"step {i}");
            clock.Stop();
            Assert.False(disk.Released.IsSet);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"logging took {clock.Elapsed} with the disk stuck");

            disk.Released.Set();
            await file.WhenWrittenAsync().WaitAsync(Patience);

            string[] steps = Regex.Matches(disk.Text, @"Walker: step (\d+)").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString()), steps);
            Assert.StartsWith("# MudPlay debug log — topic=program", disk.Text);
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task AStuckDisk_DropsTheOldestBeyondTheCap_AndTheFileSaysHowMany()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        Disks disks = new() { Make = () => disk };
        using ProgramLogFile file = Make(log, disks);
        const int extra = 300;
        try
        {
            Assert.True(disk.Stuck.Wait(Patience));
            int total = ProgramLogFile.QueueDepth + extra;
            for (int i = 0; i < total; i++) log.Info("Walker", $"step {i}");

            disk.Released.Set();
            await file.WhenWrittenAsync().WaitAsync(Patience);

            string text = disk.Text;
            Match note = Assert.Single(Regex.Matches(text, @"^# (\d+) entries dropped while the disk was busy\r?$", RegexOptions.Multiline));
            int dropped = int.Parse(note.Groups[1].Value);
            // The barrier this test waits on may itself have pushed one more out.
            Assert.InRange(dropped, extra, extra + 1);
            int written = Regex.Matches(text, @"Walker: step \d+").Count;
            Assert.Equal(total, written + dropped);
            // What went is the oldest; the end of the log is all there, after the note.
            Assert.DoesNotContain("Walker: step 0\n", text.Replace("\r", ""));
            Assert.Contains($"Walker: step {total - 1}", text);
            Assert.True(note.Index < text.IndexOf("Walker: step", StringComparison.Ordinal));
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task AFailedWrite_IsNotedOnce_NeverThroughItsOwnFile_AndAgainOnlyAfterAWriteSucceeds()
    {
        LogService log = new();
        int failing = 1;
        Disks disks = new() { Make = () => new TestDisk { FailFlush = () => Volatile.Read(ref failing) == 1 } };
        using ProgramLogFile file = Make(log, disks, retryDelay: TimeSpan.FromMilliseconds(20));

        // Two batches that both fail: one notice.
        log.Info("Walker", "first");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        log.Info("Walker", "second");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.True(disks.Opened.Count >= 2, "the file was not opened again after the failure");
        LogEntry notice = Assert.Single(log.Snapshot(), e => e.Source == ProgramLogFile.LogSource);
        Assert.Equal(LogSeverity.Warn, notice.Severity);
        Assert.Contains("IOException: disk full", notice.Message);

        // The disk answers: the file says where writing starts again.
        Volatile.Write(ref failing, 0);
        log.Info("Walker", "third");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        string resumed = disks.Opened.Last().Text;
        Assert.Contains("# writing failed (IOException: disk full) and starts again here: up to", resumed);
        Assert.Contains("Walker: third", resumed);
        Assert.Contains(log.Snapshot(), e => e.Source == ProgramLogFile.LogSource && e.Severity == LogSeverity.Info);

        // A write succeeded in between, so a new failure gets a notice of its own.
        Volatile.Write(ref failing, 1);
        log.Info("Walker", "fourth");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(2, log.Snapshot().Count(e => e.Source == ProgramLogFile.LogSource && e.Severity == LogSeverity.Warn));

        // The notices never reach the file they report on, and every reopen is the
        // same session's file.
        Assert.DoesNotContain(ProgramLogFile.LogSource + ":", disks.AllText);
        Assert.Single(disks.Paths.Distinct());
    }

    [Fact]
    public void Exit_WaitsForASlowDisk_ButNoLongerThanTheBoundForAStuckOne()
    {
        // Slow: answers after a moment, inside the bound, and everything lands.
        LogService slowLog = new();
        TestDisk slow = new() { StallFirstWrite = true };
        ProgramLogFile slowFile = Make(slowLog, new Disks { Make = () => slow });
        Assert.True(slow.Stuck.Wait(Patience));
        for (int i = 0; i < 50; i++) slowLog.Info("Walker", $"step {i}");
        using Timer answer = new(_ => slow.Released.Set(), null, 300, Timeout.Infinite);
        slowFile.Dispose();
        Assert.Contains("Walker: step 49", slow.Text);

        // Stuck for good: exit goes on once the bound is up, and says so in the log.
        LogService stuckLog = new();
        TestDisk stuck = new() { StallFirstWrite = true };
        ProgramLogFile stuckFile = Make(stuckLog, new Disks { Make = () => stuck });
        try
        {
            Assert.True(stuck.Stuck.Wait(Patience));
            stuckLog.Info("Walker", "never written");
            Stopwatch clock = Stopwatch.StartNew();
            stuckFile.Dispose();
            clock.Stop();
            Assert.InRange(clock.Elapsed, ProgramLogFile.ExitDrainBound - TimeSpan.FromMilliseconds(100),
                ProgramLogFile.ExitDrainBound + TimeSpan.FromSeconds(2));
            Assert.Contains(stuckLog.Snapshot(), e => e.Source == ProgramLogFile.LogSource && e.Message.Contains("still being written"));
        }
        finally { stuck.Released.Set(); }
    }

    [Fact]
    public void TheCrashWait_LandsWhatWasLogged_AndGivesUpOnAStuckDiskAtItsBound()
    {
        LogService log = new();
        TestDisk disk = new() { StallFirstWrite = true };
        using ProgramLogFile file = Make(log, new Disks { Make = () => disk });
        try
        {
            Assert.True(disk.Stuck.Wait(Patience));
            log.Error("Combat", "the line before the crash");

            Stopwatch clock = Stopwatch.StartNew();
            Assert.False(file.WaitWritten(TimeSpan.FromMilliseconds(300)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the stuck wait took {clock.Elapsed}");

            disk.Released.Set();
            Assert.True(file.WaitWritten(ProgramLogFile.CrashDrainBound));
            Assert.Contains("[Error] Combat: the line before the crash", disk.Text);
        }
        finally { disk.Released.Set(); }
    }

    [Fact]
    public async Task TheSwitch_OpensAFreshFileEachTimeItGoesOn_AndClosesItWhenOff()
    {
        LogService log = new();
        LogDiagnosticState diagnostics = new();
        Disks disks = new();
        using ProgramLogFile file = Make(log, disks, diagnostics: diagnostics);

        log.Info("Walker", "before");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Empty(disks.Opened);
        Assert.Null(file.CurrentPath);

        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "during");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        TestDisk first = Assert.Single(disks.Opened);
        string firstPath = Assert.Single(disks.Paths);
        Assert.Equal(firstPath, file.CurrentPath);
        Assert.StartsWith(AppPaths.LogsDir, firstPath);
        Assert.EndsWith($"-p{Environment.ProcessId}-program.log", firstPath);

        diagnostics.AutoCollectLogs = false;
        Assert.Null(file.CurrentPath);
        log.Info("Walker", "while off");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.True(first.Disposed);
        Assert.Contains("Walker: during", first.Text);
        Assert.DoesNotContain("while off", disks.AllText);

        diagnostics.AutoCollectLogs = true;
        log.Info("Walker", "again");
        await file.WhenWrittenAsync().WaitAsync(Patience);
        Assert.Equal(2, disks.Opened.Count);
        TestDisk second = disks.Opened.Last();
        Assert.StartsWith("# MudPlay debug log — topic=program", second.Text);
        Assert.Contains("Walker: again", second.Text);
        Assert.DoesNotContain("again", first.Text);
        Assert.Equal(disks.Paths.Last(), file.CurrentPath);
    }

    [Fact]
    public async Task WithTheRealDisk_TheSessionFileUnderLogsHoldsTheHeaderAndTheEntries()
    {
        ScratchRoot.Require();
        LogService log = new();
        string? path = null;
        ProgramLogFile file = new(log, new LogDiagnosticState { AutoCollectLogs = true });
        try
        {
            log.Warn("Telnet", "link dropped");
            await file.WhenWrittenAsync().WaitAsync(Patience);
            path = file.CurrentPath;
            Assert.NotNull(path);
            Assert.Equal(AppPaths.LogsDir, Path.GetDirectoryName(path));

            using FileStream stream = new(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string text = new StreamReader(stream).ReadToEnd();
            Assert.StartsWith("# MudPlay debug log — topic=program", text);
            Assert.Matches(@"\d\d:\d\d:\d\d\.\d{3} \[Warn\] Telnet: link dropped", text);
        }
        finally
        {
            file.Dispose();
            if (path is not null) File.Delete(path);
        }
    }

    // A file that keeps what is written to it. StallFirstWrite holds the first
    // string written until Released; FailFlush makes a flush throw a disk error.
    private sealed class TestDisk : TextWriter
    {
        private readonly StringBuilder _text = new();
        private int _writes;
        public bool StallFirstWrite { get; init; }
        public Func<bool>? FailFlush { get; init; }
        public ManualResetEventSlim Stuck { get; } = new();
        public ManualResetEventSlim Released { get; } = new();
        public bool Disposed { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public string Text { get { lock (_text) { return _text.ToString(); } } }

        public override void Write(char value)
        {
            lock (_text) { _text.Append(value); }
        }

        public override void Write(string? value)
        {
            if (StallFirstWrite && Interlocked.Increment(ref _writes) == 1)
            {
                Stuck.Set();
                Released.Wait();
            }
            lock (_text) { _text.Append(value); }
        }

        public override void Flush()
        {
            if (FailFlush?.Invoke() == true) throw new IOException("disk full");
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
