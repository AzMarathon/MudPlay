using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The memory log (Auto-collect logs): a file that opens with what wrote it and
// whose session it is, then a sample a minute carrying the native figure, what
// holds native memory, the work done since the last sample, what the windows
// hold and which are open. Driven on a manual clock and timers; the figures are
// the test's, the window contents and activity counts the client's own types.
public sealed class MemoryUsageLogTests
{
    private const long Mb = 1024 * 1024;

    private sealed class Rig : IAsyncDisposable
    {
        public ManualClock Clock { get; } = new();
        public ManualTimers Timers { get; } = new();
        public LogDiagnosticState Diagnostics { get; } = new();
        public ContentSizes Contents { get; } = new();
        public ActivityCounters Activity { get; } = new();
        public MemoryUsageLog Log { get; }
        public List<int> ConversationRows { get; } = [1, 2, 3];
        public LogFileHeader.Session Session { get; set; } = new("Fujin", "Mudbbs", "paradigm", "Paradigm 1.11");
        public string Windows { get; set; } = "Main, Conversation";
        public long PrivateMb = 2000, CommittedMb = 500;
        public int? Gdi;

        public Rig()
        {
            ScratchRoot.Require();
            Contents.Track("convo", () => ConversationRows.Count);
            Log = new MemoryUsageLog(Diagnostics, new MemoryUsageLog.Sources(Read, () => Session, () => Clock.Now, Timers.Arm));
        }

        private MemoryUsageLog.Sample Read() => new(
            new HangReport.Memory(300 * Mb, 9000 * Mb, 2500 * Mb, PrivateMb * Mb, 320 * Mb, CommittedMb * Mb, 40, 12, 3),
            new ProcessResources(PrivateMb * Mb, CommittedMb * Mb, 20 * Mb, 150 * Mb, 60 * Mb, 5 * Mb, 1 * Mb,
                PinnedObjects: 7, GcHandles: 1800, Threads: 42, Handles: 310, PoolThreads: 6, Timers: 25,
                GdiObjects: Gdi, UserObjects: Gdi is null ? null : 120, SkiaCacheBytes: 64 * Mb, SkiaCacheLimit: 256 * Mb,
                FontCacheBytes: 2 * Mb, FontCacheLimit: 2 * Mb, FontCacheEntries: 31),
            Windows, Contents.Describe(), Activity.Read());

        // A minute on the clock and the timers.
        public void Minute()
        {
            Clock.Advance(MemoryUsageLog.SampleInterval);
            Timers.Advance(MemoryUsageLog.SampleInterval);
        }

        public string[] Lines => File.ReadAllLines(Log.CurrentPath!);

        public async ValueTask DisposeAsync()
        {
            string? path = Log.CurrentPath;
            await Log.DisposeAsync();
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Nothing_is_written_until_auto_collect_goes_on()
    {
        await using Rig rig = new();
        rig.Minute();
        rig.Minute();
        Assert.False(rig.Log.IsCollecting);
        Assert.Null(rig.Log.CurrentPath);
        Assert.Null(rig.Log.LatestSummary);
    }

    [Fact]
    public async Task The_file_opens_with_the_build_the_process_and_the_session_then_a_baseline_sample()
    {
        await using Rig rig = new();
        rig.Diagnostics.AutoCollectLogs = true;
        rig.Timers.Advance(TimeSpan.Zero);   // the baseline, armed off the toggle's thread

        string[] lines = rig.Lines;
        Assert.Equal("# MudPlay debug log — topic=memory", lines[0]);
        Assert.Equal($"# started {rig.Clock.Now:O}", lines[1]);
        Assert.Equal("# MudPlay " + ClientEnvironment.Build, lines[2]);
        Assert.Contains(ClientEnvironment.Runtime, lines[3]);
        Assert.StartsWith($"# process {Environment.ProcessId} started ", lines[4]);
        Assert.StartsWith("# client up ", lines[5]);
        Assert.Equal("# character Fujin; BBS Mudbbs; realm paradigm; game data Paradigm 1.11", lines[6]);
        Assert.Contains("# columns: working-set", lines[7]);

        string baseline = lines[^1];
        Assert.StartsWith("12:00:00.000 ws=2500.0MB priv=2000.0MB managed=300.0MB heap=320.0MB committed=500.0MB", baseline);
        Assert.Contains("gc0=40 gc1=12 gc2=3 native=1500.0MB threads=42 handles=310 gchandles=1800 pinned=7 pool=6 timers=25 "
            + "skia=64.0MB/256.0MB fonts=2.0MB/2.0MB:31", baseline);
        Assert.DoesNotContain("gdi=", baseline);   // not Windows: left out, not zero
        Assert.DoesNotContain(" in ", baseline);   // the first sample has nothing to measure from
        Assert.EndsWith(" | convo=3 | windows Main, Conversation", baseline);
        Assert.Equal("native 1500.0MB, private 2000.0MB at 12:00", rig.Log.LatestSummary);
    }

    [Fact]
    public async Task Each_minute_says_what_went_by_since_the_last_sample_and_whose_session_it_now_is()
    {
        await using Rig rig = new();
        rig.Gdi = 900;
        rig.Diagnostics.AutoCollectLogs = true;
        rig.Timers.Advance(TimeSpan.Zero);

        for (int i = 0; i < 250; i++) rig.Activity.LineReceived();
        for (int i = 0; i < 40; i++) rig.Activity.TerminalRendered();
        rig.Activity.MapRedrawn();
        rig.PrivateMb += 12;
        rig.ConversationRows.Add(4);
        rig.Windows = "Main, Conversation, Navigation";
        rig.Session = rig.Session with { Character = "Raiden" };
        rig.Minute();

        string[] lines = rig.Lines;
        Assert.Equal("12:01:00.000 # now character Raiden; BBS Mudbbs; realm paradigm; game data Paradigm 1.11", lines[^2]);
        string sample = lines[^1];
        Assert.Contains("gdi=900 user=120", sample);
        Assert.Contains(" | in 60s: lines+250 renders+40 mapdraws+1 native+12.0MB | convo=4 | windows Main, Conversation, Navigation", sample);

        rig.Minute();
        Assert.Contains(" | in 60s: lines+0 renders+0 mapdraws+0 native+0.0MB", rig.Lines[^1]);
        Assert.DoesNotContain(rig.Lines, l => l.Contains("# now") && l.StartsWith("12:02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_off_closes_the_file_and_stops_the_samples()
    {
        await using Rig rig = new();
        rig.Diagnostics.AutoCollectLogs = true;
        rig.Timers.Advance(TimeSpan.Zero);
        string path = rig.Log.CurrentPath!;
        int written = File.ReadAllLines(path).Length;

        rig.Diagnostics.AutoCollectLogs = false;
        rig.Minute();

        Assert.Null(rig.Log.CurrentPath);
        Assert.Null(rig.Log.LatestSummary);
        Assert.Equal(written, File.ReadAllLines(path).Length);
        File.Delete(path);
    }
}
