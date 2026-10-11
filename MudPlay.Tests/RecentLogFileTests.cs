using System.Collections.Concurrent;
using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The always-written last minutes of the program log: bounded by time and size,
// written off the thread that logs, absent while Auto-collect logs is on, and
// cleaned up across client processes.
public sealed class RecentLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-recent-log-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public RecentLogFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathA => Path.Combine(_dir, "2026-10-10_12-00-00-p1-recent-a.log");
    private string PathB => Path.Combine(_dir, "2026-10-10_12-00-00-p1-recent-b.log");

    private static LogEntry Entry(DateTimeOffset at, string message) =>
        new(at, LogSeverity.Info, "Walker", message);

    private RecentLogFile Make(LogDiagnosticState? diagnostics = null, Func<string, TextWriter>? open = null) =>
        new(new LogService(), diagnostics ?? new LogDiagnosticState(), PathA, PathB, () => ["# header"], open);

    [Fact]
    public async Task KeepsBetweenOneAndTwoSpansOfTheNewestEntries()
    {
        await using RecentLogFile file = Make();

        // An entry every 10 s for 9 minutes: 4.5 spans.
        for (int i = 0; i < 54; i++) file.Enqueue(Entry(T0.AddSeconds(i * 10), $"step {i}"));
        await file.WhenWrittenAsync();

        string all = File.ReadAllText(PathA) + File.ReadAllText(PathB);
        // The newest entry is there; the first minutes are gone.
        Assert.Contains("step 53", all);
        Assert.DoesNotContain("step 0\n", all);
        int kept = Enumerable.Range(0, 54).Count(i => all.Contains($"step {i}\n"));
        int perSpan = (int)(RecentLogFile.SegmentSpan.TotalSeconds / 10);
        Assert.InRange(kept, perSpan, perSpan * 2);
        // What is kept is the newest run, with no hole in it.
        Assert.All(Enumerable.Range(54 - kept, kept), i => Assert.Contains($"step {i}\n", all));
        // Each file stands on its own.
        Assert.StartsWith("# header\n", File.ReadAllText(PathA));
        Assert.StartsWith("# header\n", File.ReadAllText(PathB));
        Assert.Equal(2, file.CurrentFiles.Count);
    }

    [Fact]
    public async Task NeverPassesTheSizeCap_HoweverFastTheLogGrows()
    {
        await using RecentLogFile file = Make();
        string big = new('x', 4000);

        // 4 MB inside one second: the time bound never fires, the size bound must.
        for (int i = 0; i < 1000; i++) file.Enqueue(Entry(T0.AddMilliseconds(i), big));
        await file.WhenWrittenAsync();

        Assert.True(new FileInfo(PathA).Length <= RecentLogFile.SegmentMaxBytes);
        Assert.True(new FileInfo(PathB).Length <= RecentLogFile.SegmentMaxBytes);
    }

    [Fact]
    public async Task NothingIsWrittenOnTheThreadThatLogs()
    {
        ConcurrentBag<int> writerThreads = new();
        await using RecentLogFile file = Make(open: _ => new ThreadRecordingWriter(writerThreads));
        int loggingThread = Environment.CurrentManagedThreadId;

        for (int i = 0; i < 200; i++) file.Enqueue(Entry(T0.AddSeconds(i), $"step {i}"));
        await file.WhenWrittenAsync();

        Assert.NotEmpty(writerThreads);
        Assert.DoesNotContain(loggingThread, writerThreads);
    }

    [Fact]
    public async Task AutoCollectLogsOn_WritesNoFile_AndResumesWhenItGoesOff()
    {
        LogDiagnosticState diagnostics = new() { AutoCollectLogs = true };
        await using RecentLogFile file = Make(diagnostics);

        file.Enqueue(Entry(T0, "while the full log is the record"));
        await file.WhenWrittenAsync();
        Assert.False(File.Exists(PathA));
        Assert.False(File.Exists(PathB));
        Assert.Empty(file.CurrentFiles);

        diagnostics.AutoCollectLogs = false;
        file.Enqueue(Entry(T0.AddSeconds(1), "back on"));
        await file.WhenWrittenAsync();
        Assert.Contains("back on", File.ReadAllText(Assert.Single(file.CurrentFiles)));
    }

    [Fact]
    public async Task LogServiceEntries_ReachTheFile()
    {
        LogService log = new();
        await using RecentLogFile file = new(log, new LogDiagnosticState(), PathA, PathB, () => ["# header"]);

        log.Warn("Telnet", "link dropped");
        await file.WhenWrittenAsync();

        Assert.Contains("[Warn] Telnet: link dropped", File.ReadAllText(Assert.Single(file.CurrentFiles)));
    }

    [Fact]
    public void Prune_KeepsTheNewestProcessesPairsAndLeavesOtherLogsAlone()
    {
        string other = Path.Combine(_dir, "2026-10-01_00-00-00-p9-program.log");
        File.WriteAllText(other, "x");
        for (int p = 1; p <= 5; p++)
        {
            foreach (string half in new[] { "a", "b" })
            {
                string path = Path.Combine(_dir, $"2026-10-0{p}_00-00-00-p{p}-recent-{half}.log");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, p, 0, 0, 0, DateTimeKind.Utc));
            }
        }

        int deleted = RecentLogFile.PruneOldProcesses(_dir, keep: 3);

        Assert.Equal(4, deleted);
        string[] left = Directory.GetFiles(_dir, "*-recent-?.log").Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(6, left.Length);
        Assert.All(left, name => Assert.DoesNotMatch("-p[12]-", name));
        Assert.True(File.Exists(other));
    }

    private sealed class ThreadRecordingWriter(ConcurrentBag<int> threads) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => threads.Add(Environment.CurrentManagedThreadId);
        public override void Write(string? value) => threads.Add(Environment.CurrentManagedThreadId);
        public override void Flush() => threads.Add(Environment.CurrentManagedThreadId);
    }
}
