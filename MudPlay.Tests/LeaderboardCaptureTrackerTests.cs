using System.Reflection;
using System.Text;
using MudPlay.Game.Leaderboard;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// LeaderboardCaptureTracker: the state machine that stitches a live "top N" block
// off LineExtractor.LineEmitted and hands the finished snapshot to the per-BBS
// store. Drives the tracker the same way GroundItemTrackerTests does — invoking
// the LineEmitted delegate by reflection so no real terminal feed is needed.
public sealed class LeaderboardCaptureTrackerTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 20, 20, 3, 25, TimeSpan.Zero);
    private const int NameCol = 5, ClassCol = 27, GuildCol = 38, ExpCol = 58;

    private static string BuildHeader()
    {
        var sb = new StringBuilder();
        Place(sb, "Rank", 0);
        Place(sb, "Name", NameCol);
        Place(sb, "Class", ClassCol);
        Place(sb, "Gang/Guild", GuildCol);
        Place(sb, "Experience", ExpCol);
        return sb.ToString();
    }

    private static string Row(int rank, string name, string cls, string guild, string exp)
    {
        var sb = new StringBuilder();
        sb.Append((rank.ToString() + ".").PadLeft(4));
        Place(sb, name, NameCol);
        Place(sb, cls, ClassCol);
        Place(sb, guild, GuildCol);
        Place(sb, exp, ExpCol);
        return sb.ToString();
    }

    private static void Place(StringBuilder sb, string value, int start)
    {
        while (sb.Length < start) sb.Append(' ');
        sb.Append(value);
    }

    private static void Feed(LineExtractor lines, string text, bool isPrompt = false)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(), T, isPrompt));
        }
    }

    private static (LeaderboardCaptureTracker tracker, LeaderboardSnapshotStore store, LineExtractor lines) Setup()
    {
        var store = new LeaderboardSnapshotStore(); // no BBS pinned → in-memory, Persist no-ops
        var tracker = new LeaderboardCaptureTracker(store);
        var lines = new LineExtractor(new TerminalEmulator(80, 24));
        tracker.AttachLineExtractor(lines);
        return (tracker, store, lines);
    }

    [Fact]
    public void CapturesBlock_TerminatedByPrompt_WithRequestedCount()
    {
        var (_, store, lines) = Setup();

        Feed(lines, "top 100");                 // echoed request → RequestedCount
        Feed(lines, "Top Heroes of the Realm"); // ignored while idle
        Feed(lines, BuildHeader());             // begins collecting
        Feed(lines, "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-"); // underline before rows — skipped
        Feed(lines, Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        Feed(lines, Row(2, "Shooting Loads", "Ranger", "what happen", "1522027759"));
        Feed(lines, "[HP=91/KAI=10]:", isPrompt: true); // terminates block

        Assert.Single(store.Snapshots);
        LeaderboardSnapshot snap = store.Snapshots[0];
        Assert.Equal(100, snap.RequestedCount);
        Assert.Equal(2, snap.Entries.Count);
        Assert.Equal("Salty Exp", snap.Entries[0].Name);
        Assert.Equal("Shooting Loads", snap.Entries[1].Name);
        Assert.Equal(T, snap.CapturedAtUtc);
    }

    [Fact]
    public void CapturesBlock_TerminatedByNonRowLine()
    {
        var (_, store, lines) = Setup();

        Feed(lines, "top 50");
        Feed(lines, BuildHeader());
        Feed(lines, Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        Feed(lines, "General Store"); // non-row content ends the block once rows are in

        Assert.Single(store.Snapshots);
        Assert.Equal(50, store.Snapshots[0].RequestedCount);
        Assert.Single(store.Snapshots[0].Entries);
    }

    [Fact]
    public void TruncatedRequest_MarksSnapshotComplete()
    {
        var (_, store, lines) = Setup();

        // Asked for 200, realm returned 1 → whole pool shown (IsComplete).
        Feed(lines, "top 200");
        Feed(lines, BuildHeader());
        Feed(lines, Row(1, "Salty Exp", "Bard", "Droppin' Loads", "1529225118"));
        Feed(lines, "[HP=91/KAI=10]:", isPrompt: true);

        Assert.Single(store.Snapshots);
        Assert.Equal(200, store.Snapshots[0].RequestedCount);
        Assert.True(store.Snapshots[0].IsComplete);
    }

    [Fact]
    public void NoHeader_NothingCaptured()
    {
        var (_, store, lines) = Setup();

        Feed(lines, "You say, 'top of the morning'");
        Feed(lines, "[HP=91/KAI=10]:", isPrompt: true);

        Assert.Empty(store.Snapshots);
    }
}
