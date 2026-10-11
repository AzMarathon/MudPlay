using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The report's program-log sections, and the Warn / Error ring behind the second.
public sealed class BugReportLogSectionsTests
{
    private static readonly BugReportChatCensor Censor = new(Array.Empty<ulong>());

    [Fact]
    public void ProblemRing_OutlivesABurstOfRoutineEntries()
    {
        LogService log = new(capacity: 50);
        log.Warn("Walker", "the one that mattered");
        for (int i = 0; i < 500; i++) log.Info("Noise", $"routine {i}");

        Assert.DoesNotContain(log.Snapshot(), e => e.Severity == LogSeverity.Warn);
        LogEntry kept = Assert.Single(log.SnapshotProblems());
        Assert.Equal("the one that mattered", kept.Message);
    }

    [Fact]
    public void ProblemRing_KeepsTheNewestInOrder()
    {
        LogService log = new();
        for (int i = 0; i < LogService.ProblemCapacity + 25; i++) log.Error("Engine", $"e{i}");

        LogEntry[] kept = log.SnapshotProblems();
        Assert.Equal(LogService.ProblemCapacity, kept.Length);
        Assert.Equal("e25", kept[0].Message);
        Assert.Equal($"e{LogService.ProblemCapacity + 24}", kept[^1].Message);
    }

    [Fact]
    public void Clear_EmptiesBothRingsAndIsRemembered()
    {
        LogService log = new();
        log.Warn("Walker", "w");
        Assert.Null(log.LastClearedAt);

        log.Clear();

        Assert.Empty(log.SnapshotProblems());
        Assert.NotNull(log.LastClearedAt);
    }

    [Fact]
    public void ProgramLog_WritesTheWholeRingWithMillisecondsAndContextOnWarnings()
    {
        LogService log = new(capacity: 100) { Diagnostics = AllOn() };
        for (int i = 0; i < 100; i++) log.Info("Walker", $"step {i}");
        log.Warn("RoomClassifier", "unknown entity", "Also here: a thing.");

        BugReportBuilder.Section section = BugReportLogSections.ProgramLog(log, Censor);

        Assert.Equal(100, section.Entries!.Count);
        Assert.Contains("100 entries, all the ring of 100 holds; 101 logged this session.", section.Body);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d  \[Info\]  Walker: step 1$", section.Entries[0]);
        Assert.EndsWith("RoomClassifier: unknown entity\n              context: Also here: a thing.", section.Entries[^1]);
        Assert.Equal(BugReportLogSections.ProgramLogWeight, section.BudgetWeight);
        Assert.DoesNotContain("diagnostics were off", section.Body);
    }

    [Fact]
    public void ProgramLog_SaysWhichChannelWasOffAndThatTheLogWasCleared()
    {
        LogDiagnosticState diagnostics = AllOn();
        diagnostics.CombatDiagnostics = false;
        LogService log = new() { Diagnostics = diagnostics };
        log.Info("Walker", "before");
        log.Clear();
        log.Info("Walker", "after");

        BugReportBuilder.Section section = BugReportLogSections.ProgramLog(log, Censor);

        Assert.Contains("> Combat diagnostics were off", section.Body);
        Assert.Contains("The log was cleared by hand at", section.Body);
        Assert.Single(section.Entries!);
    }

    [Fact]
    public void ProgramLog_KeepsAnEntryWithALineBreakOnOneLine()
    {
        LogService log = new() { Diagnostics = AllOn() };
        log.Info("CombatProfiles", "first\nsecond");

        BugReportBuilder.Section section = BugReportLogSections.ProgramLog(log, Censor);

        Assert.DoesNotContain('\n', Assert.Single(section.Entries!));
    }

    [Fact]
    public void Problems_ListsWarnAndErrorOnly()
    {
        LogService log = new();
        log.Info("Walker", "stepped north");
        log.Warn("Walker", "held");
        log.Error("Telnet", "dropped");

        BugReportBuilder.Section section = BugReportLogSections.Problems(log, Censor);

        Assert.Contains("[Warn]  Walker: held", section.Body);
        Assert.Contains("[Error]  Telnet: dropped", section.Body);
        Assert.DoesNotContain("stepped north", section.Body);
    }

    private static LogDiagnosticState AllOn() => new() { DebugDiagnostics = true, CombatDiagnostics = true };
}
