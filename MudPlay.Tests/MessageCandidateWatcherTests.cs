using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MessageCandidateWatcherTests
{
    private sealed class Harness
    {
        public LogService Log { get; } = new();
        public MessageRouter Router { get; } = new();
        public MessageStore Messages { get; } = new();
        public MessageCandidateStore Candidates { get; } = new();
        public MessageCandidateWatcher Watcher { get; }

        public Harness()
        {
            Watcher = new MessageCandidateWatcher(Router, Messages, Candidates, Log);
        }

        // The watcher subscribes to LineExtractor in real life; tests reflect into
        // the private OnLine directly instead of standing up a fake extractor —
        // same pattern ConditionTrackerTests uses for the identical shape.
        public void Feed(string text, DateTimeOffset? when = null)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                when ?? DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(MessageCandidateWatcher)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Watcher, new object[] { emitted });
        }
    }

    private static MessageRecord MakeRecord(string casterMessage) => new(
        Id: MessageRecord.ComputeId("Test", casterMessage, "", "", "", ""),
        Name: "Test",
        Action: MessageAction.Ignore,
        Flags: MessageFlags.None,
        RawFlagsHex: 0,
        Response: string.Empty,
        CasterMessage: casterMessage,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: string.Empty,
        AppliedEndsWith: string.Empty);

    [Fact]
    public void KnownMessageLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeRecord("You feel a surge of power!"));

        h.Feed("You feel a surge of power!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void RouterMatchedLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Router.RegisterPattern(new PrefixPattern("test.gossip", "*GOSSIP* "));

        h.Feed("*GOSSIP* Forged: hello");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void GenuinelyNewLine_CreatesCandidate_AndWarnsOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void RepeatedLine_BumpsOccurrences_WarnsOnlyOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(3, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void DisabledWatcher_NeverCreatesCandidates()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Feed("Whatever this is, it should be ignored.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BurstOfDistinctUnrecognizedLines_CapsAtBurstLimit()
    {
        // BurstCap = 6, BurstWindow = 1500ms: 10 distinct never-seen lines
        // arriving within the window should stage only the first 6.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
            h.Feed($"Distinct never-seen line #{i}", t0.AddMilliseconds(i * 50));

        Assert.Equal(6, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void BurstAcrossTwoWindows_BothGroupsStageNormally()
    {
        // Two separate bursts of 5 (under the cap), well apart in time, should
        // each stage in full — the window resets rather than accumulating
        // across the gap.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            h.Feed($"Group A line #{i}", t0.AddMilliseconds(i * 50));

        DateTimeOffset t1 = t0.AddSeconds(2);   // past the 1500ms burst window
        for (int i = 0; i < 5; i++)
            h.Feed($"Group B line #{i}", t1.AddMilliseconds(i * 50));

        Assert.Equal(10, h.Candidates.Candidates.Count);
    }
}
