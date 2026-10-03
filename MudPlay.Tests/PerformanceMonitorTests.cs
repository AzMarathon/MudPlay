using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class PerformanceMonitorTests
{
    private static (PerformanceMonitor Monitor, List<string> Lines) Collecting()
    {
        PerformanceMonitor monitor = new(null, () => true);
        List<string> lines = new();
        monitor.CollectTo(lines.Add);
        return (monitor, lines);
    }

    [Fact]
    public void Nothing_is_timed_while_not_collecting()
    {
        PerformanceMonitor monitor = new(null, () => true);
        using (monitor.Measure("terminal feed")) { }

        Assert.DoesNotContain("terminal feed", monitor.Summary());
    }

    [Fact]
    public void A_summary_gives_each_kind_of_work_as_count_p50_p95_max_and_then_starts_over()
    {
        (PerformanceMonitor monitor, _) = Collecting();
        for (int i = 1; i <= 100; i++) monitor.Record("terminal feed", i);

        Assert.Contains("| terminal feed 100/51.0/95.0/100.0 |", monitor.Summary());
        Assert.DoesNotContain("terminal feed", monitor.Summary());
    }

    [Fact]
    public void Each_stall_is_written_with_its_cause_and_summed_up_for_the_minute()
    {
        (PerformanceMonitor monitor, List<string> lines) = Collecting();
        monitor.RecordStall(60, "terminal feed");
        monitor.RecordStall(300, "opening SettingsWindow");
        monitor.RecordStall(1200, "parse Rooms");

        Assert.Equal(new[] { "stall 60 ms  terminal feed", "stall 300 ms  opening SettingsWindow", "stall 1200 ms  parse Rooms" }, lines);
        Assert.Contains("stalls 3 (2 over 250 ms, 1 over 1 s), worst 1200 ms, 1560 ms in all", monitor.Summary());
    }

    [Fact]
    public void A_measured_scope_lands_in_the_summary()
    {
        (PerformanceMonitor monitor, _) = Collecting();
        using (monitor.Measure("profile save"))
        using (monitor.Measure("parse Items")) { }

        string summary = monitor.Summary();

        Assert.Contains("| profile save 1/", summary);
        Assert.Contains("| parse Items 1/", summary);
    }
}
