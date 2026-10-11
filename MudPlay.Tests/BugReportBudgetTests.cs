using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The report's size budget: history sections give up their oldest entries, the
// wire sections are neither counted nor trimmed, and the result says what went.
public sealed class BugReportBudgetTests
{
    private static List<string> Entries(int count, int bytesEach) =>
        Enumerable.Range(0, count).Select(i => i.ToString("D6") + new string('x', bytesEach - 7)).ToList();

    [Fact]
    public void UnderBudget_NothingIsTrimmed()
    {
        BugReportBuilder.Section log = new("Program log", "intro", Entries(100, 100));

        BugReportBudget.Result result = BugReportBudget.Fit([log], maxBytes: 100_000);

        Assert.False(result.OverBudget);
        Assert.Equal(0, result.Sections[0].TrimmedEntries);
        Assert.Equal(100, result.Sections[0].Entries!.Count);
    }

    [Fact]
    public void OverBudget_TheOldestEntriesOfTheLargestSectionGoFirst()
    {
        BugReportBuilder.Section fixedSection = new("Session", new string('s', 10_000));
        BugReportBuilder.Section log = new("Program log", "intro", Entries(2_000, 100));
        BugReportBuilder.Section trail = new("Commands sent", "intro", Entries(200, 50));

        BugReportBudget.Result result = BugReportBudget.Fit([fixedSection, log, trail], maxBytes: 100_000);

        Assert.False(result.OverBudget);
        Assert.True(result.Bytes <= 100_000);
        BugReportBuilder.Section fittedLog = result.Sections[1];
        Assert.True(fittedLog.TrimmedEntries > 0);
        // The newest entry survives; the oldest is the one that went.
        Assert.StartsWith("001999", fittedLog.Entries![^1]);
        Assert.StartsWith(fittedLog.TrimmedEntries.ToString("D6"), fittedLog.Entries[0]);
        // The small section was never the largest, so it is whole.
        Assert.Equal(0, result.Sections[2].TrimmedEntries);
        Assert.Equal(fixedSection, result.Sections[0]);
    }

    [Fact]
    public void Weight_LetsASectionStayLargerBeforeItIsTrimmed()
    {
        BugReportBuilder.Section log = new("Program log", "", Entries(1_000, 100), BudgetWeight: 3);
        BugReportBuilder.Section scrollback = new("Scrollback", "", Entries(1_000, 100));

        BugReportBudget.Result result = BugReportBudget.Fit([log, scrollback], maxBytes: 120_000);

        int logKept = result.Sections[0].Entries!.Count;
        int scrollKept = result.Sections[1].Entries!.Count;
        Assert.True(result.Bytes <= 120_000);
        Assert.True(logKept > scrollKept * 2, $"log kept {logKept}, scrollback kept {scrollKept}");
    }

    [Fact]
    public void WireSections_AreNotCountedAndNotTrimmed()
    {
        BugReportBuilder.Section log = new("Program log", "", Entries(500, 100));
        BugReportBuilder.Section wire = new("Wire — raw", new string('w', 5_000_000), InBudget: false);

        BugReportBudget.Result result = BugReportBudget.Fit([log, wire], maxBytes: 100_000);

        Assert.False(result.OverBudget);
        Assert.Equal(0, result.Sections[0].TrimmedEntries);
        Assert.Equal(wire, result.Sections[1]);
    }

    [Fact]
    public void AHistoryKeepsItsNewestEntriesEvenWhenTheFixedSectionsAloneAreTooLarge()
    {
        BugReportBuilder.Section huge = new("Settings", new string('s', 200_000));
        BugReportBuilder.Section log = new("Program log", "", Entries(1_000, 100));

        BugReportBudget.Result result = BugReportBudget.Fit([huge, log], maxBytes: 100_000);

        Assert.True(result.OverBudget);
        Assert.Equal(BugReportBudget.KeepAtLeast, result.Sections[1].Entries!.Count);
    }

    [Fact]
    public void Render_OversizedCapture_StaysUnderTheBudgetWithoutTheWireAndSaysWhatWasLeftOut()
    {
        // Far more than any ring holds: 30,000 log entries, 20,000 scrollback rows.
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero),
            RealmType.Stock,
            [
                new BugReportBuilder.Section("Session", new string('s', 300_000)),
                new BugReportBuilder.Section("Program log (oldest first)", "intro", Entries(30_000, 110), BudgetWeight: 3),
                new BugReportBuilder.Section("Commands sent (oldest first)", "intro", Entries(600, 60)),
                new BugReportBuilder.Section("Scrollback (oldest first)", "intro", Entries(20_000, 50)),
                new BugReportBuilder.Section("Wire — raw", new string('w', 400_000), InBudget: false),
            ]);

        string md = BugReportBuilder.Render(capture, "it stopped");

        int withoutWire = Encoding.UTF8.GetByteCount(md) - 400_000;
        Assert.True(withoutWire <= BugReportBudget.MaxBytes + 1_000, $"report is {withoutWire} bytes without the wire");
        Assert.Contains("## Program log (oldest first) — ", md);
        Assert.Contains("oldest entries left out to keep the report under 1000 KB", md);
        // The newest entries of both histories are there, the wire is whole.
        Assert.Contains("029999", md);
        Assert.Contains("019999", md);
        Assert.Contains(new string('w', 400_000), md);
    }
}
