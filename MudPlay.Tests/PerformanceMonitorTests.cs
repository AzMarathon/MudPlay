using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class PerformanceMonitorTests
{
    private static PerformanceMonitor Monitor() => new(null, null, () => true, startWatcher: false);

    [Fact]
    public void Timings_are_summarised_by_count_percentiles_and_maximum()
    {
        using PerformanceMonitor monitor = Monitor();
        for (int i = 1; i <= 100; i++) monitor.Record("terminal feed", i);

        string report = monitor.Describe();

        Assert.Contains("| terminal feed | 100 | 51.0 | 95.0 | 100.0 |", report);
    }

    [Fact]
    public void Stalls_are_counted_by_length_and_listed_with_their_cause()
    {
        using PerformanceMonitor monitor = Monitor();
        monitor.RecordStall(60, "terminal feed");
        monitor.RecordStall(300, "opening SettingsWindow");
        monitor.RecordStall(1200, "parse Rooms");

        string report = monitor.Describe();

        Assert.Contains("3 over 50 ms (2 over 250 ms, 1 over 1 s); worst 1200 ms", report);
        Assert.Contains("300 ms  opening SettingsWindow", report);
        Assert.Contains("1200 ms  parse Rooms", report);
    }

    [Fact]
    public void A_measured_scope_lands_in_the_timings()
    {
        using PerformanceMonitor monitor = Monitor();
        using (monitor.Measure("profile save"))
        using (monitor.Measure("parse Items")) { }

        string report = monitor.Describe();

        Assert.Contains("| profile save | 1 |", report);
        Assert.Contains("| parse Items | 1 |", report);
    }
}
