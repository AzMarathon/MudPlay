using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the rolling-log primitive that backs the per-character talk.log /
// transactions.log: the on-disk line count never exceeds the cap, content
// persists across a reopen (a restart), and Truncate wipes the file. Appends
// persist asynchronously (off the caller's thread), so a test that reads the file
// right after appending calls Flush first to force the pending write. The failure
// tests swap in a writer that throws or hangs: Close / Truncate / SetMaxLines run
// on the UI thread and must never wait on a broken writer for good.
public sealed class RollingLogFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"mudplay-rolllog-{Guid.NewGuid():N}.log");

    // Runs the call off the test thread so a hang fails the test instead of the run.
    private static async Task<bool> ReturnsPromptly(Action call)
    {
        Task run = Task.Run(call);
        return await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))) == run;
    }

    [Fact]
    public void Append_KeepsOnlyLastNLines()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 3);
            for (int i = 1; i <= 10; i++) log.Append($"line {i}");
            log.Flush();

            Assert.Equal(new[] { "line 8", "line 9", "line 10" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Snapshot_IsCurrentImmediately_EvenBeforeTheDiskWrite()
    {
        // The in-memory tail (what the Conversation window seeds from) updates
        // synchronously on Append; only the file write is deferred.
        string path = TempPath();
        try
        {
            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 5);
            log.Append("a");
            log.Append("b");

            Assert.Equal(new[] { "a", "b" }, log.Snapshot());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reopen_ReloadsTail_AndContinuesAppending()
    {
        string path = TempPath();
        try
        {
            RollingLogFile first = new(new LogService());
            first.Open(path, maxLines: 5);
            first.Append("a");
            first.Append("b");
            first.Close();

            // Simulate a restart: a fresh instance over the same file continues
            // where the old one left off rather than starting empty.
            RollingLogFile second = new(new LogService());
            second.Open(path, maxLines: 5);
            second.Append("c");
            second.Flush();

            Assert.Equal(new[] { "a", "b", "c" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_TrimsAnOverlongExistingFileToCap()
    {
        string path = TempPath();
        try
        {
            File.WriteAllLines(path, new[] { "1", "2", "3", "4", "5" });

            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 2);
            log.Append("6");
            log.Flush();

            Assert.Equal(new[] { "5", "6" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetMaxLines_ShrinkingCapDropsOldestImmediately()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 5);
            foreach (string s in new[] { "a", "b", "c", "d" }) log.Append(s);

            log.SetMaxLines(2);

            Assert.Equal(new[] { "c", "d" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Truncate_EmptiesTheFile()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 5);
            log.Append("a");
            log.Append("b");

            log.Truncate();

            Assert.Empty(File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_AfterClose_IsIgnored()
    {
        string path = TempPath();
        try
        {
            RollingLogFile log = new(new LogService());
            log.Open(path, maxLines: 5);
            log.Append("a");
            log.Close();
            log.Append("b"); // no open file → dropped

            Assert.Equal(new[] { "a" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BackgroundWriteThrowing_IsLogged_AndCloseStillReturns()
    {
        string path = TempPath();
        LogService logs = new();
        using ManualResetEventSlim failing = new();
        int calls = 0;
        // The first (background) write fails with a non-IO error; later writes succeed.
        void Write(string p, string[] lines)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failing.Set();
                throw new InvalidOperationException("writer broke");
            }
            File.WriteAllLines(p, lines);
        }
        try
        {
            // A wait long enough that only a freed writer lets Close return in time.
            RollingLogFile log = new(logs, Write, TimeSpan.FromSeconds(30));
            log.Open(path, maxLines: 5);
            log.Append("a");
            // Close after the failing write began, so its own write request isn't the
            // one that fails.
            Assert.True(failing.Wait(TimeSpan.FromSeconds(5)), "background write never started");

            Assert.True(await ReturnsPromptly(log.Close), "Close blocked after the writer threw");

            LogEntry error = Assert.Single(logs.SnapshotProblems());
            Assert.Equal(LogSeverity.Error, error.Severity);
            Assert.Contains("writer broke", error.Message);
            Assert.Contains("may be unwritten", error.Message);
            // Close's own write still lands the line the failed background write dropped.
            Assert.Equal(new[] { "a" }, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BackgroundWriteThatNeverFinishes_IsCutOffByTheBoundedWait()
    {
        string path = TempPath();
        LogService logs = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int calls = 0;
        // The first (background) write hangs until the test lets it go.
        void Write(string p, string[] lines)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait();
            }
            File.WriteAllLines(p, lines);
        }
        try
        {
            RollingLogFile log = new(logs, Write, TimeSpan.FromMilliseconds(5));
            log.Open(path, maxLines: 5);
            log.Append("a");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "background write never started");

            Assert.True(await ReturnsPromptly(log.Truncate), "Truncate blocked on a stuck write");
            log.Append("b");
            log.Append("c");
            Assert.True(await ReturnsPromptly(() => log.SetMaxLines(1)), "SetMaxLines blocked on a stuck write");

            LogEntry[] problems = logs.SnapshotProblems();
            Assert.Equal(2, problems.Length);
            Assert.All(problems, e =>
            {
                Assert.Equal(LogSeverity.Warn, e.Severity);
                Assert.Contains("may be unwritten", e.Message);
            });

            // Once the disk answers, the file catches up to the current tail.
            release.Set();
            log.Flush();
            Assert.Equal(new[] { "c" }, File.ReadAllLines(path));
        }
        finally
        {
            release.Set();
            File.Delete(path);
        }
    }
}
