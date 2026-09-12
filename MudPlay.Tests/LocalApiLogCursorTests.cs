using MudPlay.Services;
using MudPlay.Services.Api;
using Xunit;

namespace MudPlay.Tests;

// The log ring's monotonic sequence + the API's severity parsing. Both exist so
// a consumer can tail the program log without re-reading or skipping entries —
// the thing that was impossible when a wedge had to be reconstructed from a
// 750-entry snapshot after the fact.
public sealed class LocalApiLogCursorTests
{
    private static LogService NewLog(int capacity = 8)
    {
        LogService log = new(capacity);
        // Debug/Combat are generation-gated on the diagnostics toggles; these
        // tests use Info so they're always recorded.
        return log;
    }

    [Fact]
    public void TotalAppended_CountsEveryEntry_NotJustRetainedOnes()
    {
        LogService log = NewLog(capacity: 4);
        for (int i = 0; i < 10; i++) log.Info("T", $"m{i}");

        Assert.Equal(10, log.TotalAppended);   // 10 appended...
        Assert.Equal(4, log.Count);            // ...4 survive the ring
    }

    [Fact]
    public void SnapshotAfter_ReturnsOnlyNewerEntries()
    {
        LogService log = NewLog();
        log.Info("T", "a");
        log.Info("T", "b");

        LogEntry[] first = log.SnapshotAfter(0, out long seq);
        Assert.Equal(2, first.Length);
        Assert.Equal(2, seq);

        log.Info("T", "c");
        LogEntry[] next = log.SnapshotAfter(seq, out long seq2);
        Assert.Equal("c", Assert.Single(next).Message);
        Assert.Equal(3, seq2);
    }

    [Fact]
    public void SnapshotAfter_AtTheHead_ReturnsNothing()
    {
        LogService log = NewLog();
        log.Info("T", "a");
        Assert.Empty(log.SnapshotAfter(log.TotalAppended, out _));
    }

    [Fact]
    public void SnapshotAfter_SurvivesRingWrap_WithoutDuplicating()
    {
        // A poller that falls behind gets what's left, not garbage or repeats.
        LogService log = NewLog(capacity: 4);
        for (int i = 0; i < 6; i++) log.Info("T", $"m{i}");

        // Sequences 1 and 2 are gone; 3..6 remain.
        LogEntry[] entries = log.SnapshotAfter(0, out long seq);
        Assert.Equal(6, seq);
        Assert.Equal(4, entries.Length);
        Assert.Equal(new[] { "m2", "m3", "m4", "m5" }, entries.Select(e => e.Message));
    }

    [Fact]
    public void SnapshotAfter_HonoursTheLimit_FromTheOldestSide()
    {
        LogService log = NewLog();
        for (int i = 0; i < 5; i++) log.Info("T", $"m{i}");

        LogEntry[] two = log.SnapshotAfter(0, out _, limit: 2);
        Assert.Equal(new[] { "m0", "m1" }, two.Select(e => e.Message));
    }

    [Fact]
    public void Clear_DoesNotRewindTheSequence()
    {
        // A consumer holding a cursor must never see it go backwards — otherwise
        // everything after a Clear looks older than what it already has and it
        // stops receiving anything.
        LogService log = NewLog();
        log.Info("T", "a");
        long before = log.TotalAppended;

        log.Clear();
        log.Info("T", "b");

        Assert.True(log.TotalAppended > before);
        Assert.Equal("b", Assert.Single(log.SnapshotAfter(before, out _)).Message);
    }

    [Fact]
    public void ParseSeverities_IsSetMembership_NotAThreshold()
    {
        // LogSeverity orders Combat AFTER Error, so a ">= Warn" threshold would
        // silently include every combat trace. The enum's own comment says
        // consumers must not compare numerically.
        var set = LocalApiServer.ParseSeverities("warn,error");

        Assert.Contains(LogSeverity.Warn, set);
        Assert.Contains(LogSeverity.Error, set);
        Assert.DoesNotContain(LogSeverity.Combat, set);
        Assert.DoesNotContain(LogSeverity.Info, set);
    }

    [Fact]
    public void ParseSeverities_EmptyOrUnrecognised_MeansNoFilter()
    {
        // A typo shouldn't turn a diagnostic request into an empty result the
        // caller might read as "nothing happened".
        Assert.Empty(LocalApiServer.ParseSeverities(null));
        Assert.Empty(LocalApiServer.ParseSeverities(""));
        Assert.Empty(LocalApiServer.ParseSeverities("wat,nope"));
    }

    [Fact]
    public void ParseSeverities_IgnoresUnknownNamesButKeepsKnownOnes()
    {
        var set = LocalApiServer.ParseSeverities("info, wat ,error");
        Assert.Equal(2, set.Count);
        Assert.Contains(LogSeverity.Info, set);
        Assert.Contains(LogSeverity.Error, set);
    }
}
