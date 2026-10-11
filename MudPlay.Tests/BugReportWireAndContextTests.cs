using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The raw wire's arrival marks, the heartbeat's cadence, and the small pieces of
// the report's client section that can be pinned without a running client.
public sealed class BugReportWireAndContextTests
{
    private static readonly BugReportChatCensor NoCensor = new(Array.Empty<ulong>());

    [Fact]
    public void WireBuffer_MarksWhereAReadBegan_AndForgetsThemWhenCleared()
    {
        WireBuffer wire = new();
        wire.Append(Encoding.Latin1.GetBytes("first line\r\n"));

        byte[] bytes = wire.Snapshot(out long start, out WireBuffer.ArrivalMark[] marks);

        Assert.Equal(12, bytes.Length);
        Assert.Equal(0, start);
        Assert.Equal(0, Assert.Single(marks).Offset);

        wire.Clear();
        wire.Snapshot(out _, out marks);
        Assert.Empty(marks);
        Assert.NotNull(wire.LastClearedAt);
    }

    [Fact]
    public void RenderRawWire_WritesTheArrivalTimeBeforeTheLineItLandedIn()
    {
        byte[] bytes = Encoding.Latin1.GetBytes("one\r\ntwo\r\nthree\r\n");
        DateTimeOffset t = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        WireBuffer.ArrivalMark[] marks =
        [
            new(1000, t),                       // the read that carried "one" and "two"
            new(1010, t.AddMilliseconds(600)),  // the read that carried "three"
        ];

        string rendered = BugReportBuilder.RenderRawWire(bytes, start: 1000, marks, clearedAt: null, NoCensor);

        Assert.Contains("--- 12:00:00.000 ---\none^M\ntwo^M\n--- 12:00:00.600 ---\nthree^M\n", rendered);
    }

    [Fact]
    public void RenderRawWire_NothingCaptured_SaysSo()
    {
        Assert.Equal("_(no wire captured)_",
            BugReportBuilder.RenderRawWire([], start: 0, [], clearedAt: null, NoCensor));
    }

    [Fact]
    public void Heartbeat_LogsOneStatusLinePerInterval()
    {
        LogService log = new();
        ClientHeartbeat heartbeat = new(log, () => "connected, in the game");
        DateTimeOffset now = DateTimeOffset.Now;

        heartbeat.Tick(now.AddSeconds(1));
        Assert.Equal(0, log.Count);

        heartbeat.Tick(now + ClientHeartbeat.Interval + TimeSpan.FromSeconds(1));
        heartbeat.Tick(now + ClientHeartbeat.Interval + TimeSpan.FromSeconds(2));

        LogEntry line = Assert.Single(log.Snapshot());
        Assert.Equal(LogSeverity.Info, line.Severity);
        Assert.StartsWith("status: up ", line.Message);
        Assert.Contains("connected, in the game", line.Message);
    }

    [Fact]
    public void Heartbeat_AStatusThatThrows_StillLogsALine()
    {
        LogService log = new();
        ClientHeartbeat heartbeat = new(log, () => throw new InvalidOperationException("mid-swap"));

        heartbeat.Tick(DateTimeOffset.Now + ClientHeartbeat.Interval + TimeSpan.FromSeconds(1));

        Assert.Contains("status unreadable: InvalidOperationException", Assert.Single(log.Snapshot()).Message);
    }

    [Fact]
    public void StartupLines_NameTheBuildTheSystemAndTheProcess()
    {
        IReadOnlyList<string> lines = ClientHeartbeat.StartupLines("data-set", "Tester");

        Assert.Contains(lines, l => l.StartsWith("MudPlay " + AppInfo.Version, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"process {Environment.ProcessId} started"));
        Assert.Contains(lines, l => l.Contains("game data data-set") && l.Contains("last profile Tester"));
    }

    [Fact]
    public void HomeRelative_HidesTheMachinesAccountName()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;   // no home folder on this machine to hide

        Assert.Equal("~" + Path.DirectorySeparatorChar + "MudPlay",
            BugReportContext.HomeRelative(Path.Combine(home, "MudPlay")));
        Assert.Equal("/somewhere/else", BugReportContext.HomeRelative("/somewhere/else"));
    }
}
