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
    public void AGiantStaticSection_IsCutDownFirst_AndTheHistoriesKeepMostOfTheBudget()
    {
        string line = new string('s', 99) + "\n";
        BugReportBuilder.Section huge = new("Monster overrides", string.Concat(Enumerable.Repeat(line, 16_000)));   // 1.6 MB
        BugReportBuilder.Section small = new("Session", new string('x', 5_000));
        BugReportBuilder.Section log = new("Program log", "", Entries(4_000, 100));                               // 400 KB

        BugReportBudget.Result result = BugReportBudget.Fit([huge, small, log], maxBytes: 1_000_000);

        Assert.False(result.OverBudget);
        Assert.True(result.Bytes <= 1_000_000);
        // The small static section is whole; the giant one says what it lost.
        Assert.Equal(small, result.Sections[1]);
        Assert.Contains("is left out to keep the report under 1000 KB", result.Sections[0].Body);
        Assert.True(result.Sections[0].Body.Length < 620_000);
        // The log was not cut to its floor to pay for it.
        Assert.True(result.Sections[2].Entries!.Count > 3_000, $"log kept {result.Sections[2].Entries!.Count}");
    }

    [Fact]
    public void ACutStaticSection_ClosesTheCodeFenceItWasCutInside()
    {
        string body = "```json\n" + string.Concat(Enumerable.Repeat("  \"k\": 1,\n", 100_000)) + "```";
        BugReportBudget.Result result = BugReportBudget.Fit([new("Settings", body)], maxBytes: 200_000);

        string cut = result.Sections[0].Body;
        Assert.Equal(1, cut.Split("```").Length % 2);   // an even number of fence marks
    }

    [Fact]
    public void Render_ATrimmedHistory_SaysHowManyAreShown()
    {
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), RealmType.Stock,
            [new BugReportBuilder.Section("Program log (oldest first)", "30000 entries.", Entries(30_000, 110))]);

        string md = BugReportBuilder.Render(capture, "x");

        Assert.Matches(@"_Shown: the newest \d+ of 30000; the first line below is the oldest kept", md);
    }

    // The budget is in bytes as the file is written: rows of box-drawing
    // characters are three bytes a character.
    [Fact]
    public void Render_MultiByteEntries_AreBudgetedByTheirBytes()
    {
        List<string> Rows(int count) =>
            Enumerable.Range(0, count).Select(i => $"{i:D6} " + new string('═', 200)).ToList();
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), RealmType.Stock,
            [
                new BugReportBuilder.Section("Scrollback (oldest first)", "intro", Rows(3_000)),
                new BugReportBuilder.Section("Program log (oldest first)", "intro", Rows(4_000), BudgetWeight: 3),
            ]);

        string md = BugReportBuilder.Render(capture, "x");

        Assert.True(md.Length < BugReportBudget.MaxBytes / 2);
        Assert.InRange(Encoding.UTF8.GetByteCount(md), BugReportBudget.MaxBytes * 9 / 10, BugReportBudget.MaxBytes);
    }

    // A mask is longer than a short value. A password that is also a common word
    // is masked hundreds of thousands of times: the budget is fitted to the
    // report as masked, so it still holds.
    [Fact]
    public void Render_APasswordThatIsACommonWord_StillFitsTheBudgetAfterMasking()
    {
        BugReportScrub.Scrubber common = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, "the")], []);
        List<string> entries = Enumerable.Range(0, 20_000)
            .Select(i => $"{i:D6} the cat sat on the mat by the door of the inn, the end").ToList();
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), RealmType.Stock,
            [new BugReportBuilder.Section("Program log (oldest first)", "body", entries, BudgetWeight: 3)], common);

        string md = BugReportBuilder.Render(capture, "x");

        Assert.InRange(Encoding.UTF8.GetByteCount(md), BugReportBudget.MaxBytes * 9 / 10, BugReportBudget.MaxBytes);
        Assert.DoesNotContain(" the ", md);
        // The newest entry is there, masked.
        Assert.Contains($"019999 {BugReportScrub.Mask} cat sat on {BugReportScrub.Mask} mat", md);
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
        // Trimmed to the budget, not far under it: nothing is thrown away for nothing.
        // (2% is kept back for the masking that follows.)
        Assert.InRange(withoutWire, BugReportBudget.MaxBytes * 97 / 100, BugReportBudget.MaxBytes);
        Assert.Contains("## Program log (oldest first) — ", md);
        Assert.Contains("oldest entries left out to keep the report under 1000 KB", md);
        // The newest entries of both histories are there, the wire is whole.
        Assert.Contains("029999", md);
        Assert.Contains("019999", md);
        Assert.Contains(new string('w', 400_000), md);
    }
}
