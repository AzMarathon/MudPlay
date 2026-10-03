using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A follower asks its leader's client where the party is heading, and the answer
// decides whether the Bossing set stays on.
public sealed class LeaderBossTravelProbeTests
{
    private static readonly RoomKey Boss = new(6, 1249);

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PathReplyTracker Replies { get; }
        public LeaderBossTravelProbe Probe { get; }
        public List<bool> Answers { get; } = new();
        public string? Leader { get; set; } = "Raijin";
        public bool StillNeeded { get; set; } = true;
        public List<TimeSpan> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Replies = new PathReplyTracker(Router, isPartyLeader: s => s == Leader);
            Probe = new LeaderBossTravelProbe(Replies,
                leaderGivenName: () => Leader,
                headsToBoss: r => r.Destination == Boss,
                isBossRoom: k => k == Boss,
                stillNeeded: () => StillNeeded,
                schedule: (delay, _) => { Scheduled.Add(delay); return new Handle(); });
            Probe.Resolved += Answers.Add;
        }

        public string LastSent => Encoding.Latin1.GetString(Probe.LastSentForTests[^1]);

        public void Line(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, [], DateTimeOffset.UtcNow, false));

        public void Dispose() { Probe.Dispose(); Replies.Dispose(); }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Ask_TelepathsTheLeader_AndABossDestinationAnswersYes()
    {
        using Harness h = new();
        Assert.True(h.Probe.Ask());
        Assert.Equal("/Raijin @path\r", h.LastSent);

        h.Line("Raijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Equal(new[] { true }, h.Answers);
        // The answer is refreshed for as long as the gear rides on it.
        Assert.Equal(LeaderBossTravelProbe.RecheckInterval, h.Scheduled[^1]);
    }

    [Fact]
    public void AnswerThatIsNotABossRoom_IsANo()
    {
        using Harness h = new();
        h.Probe.Ask();

        h.Line("Raijin telepaths: {walking to 1/5; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Equal(new[] { false }, h.Answers);
    }

    [Fact]
    public void NoAnswer_TimesOutToANo()
    {
        using Harness h = new();
        h.Probe.Ask();
        Assert.Equal(LeaderBossTravelProbe.ReplyTimeout, h.Scheduled[^1]);

        h.Probe.OnTimeout();

        Assert.Equal(new[] { false }, h.Answers);
    }

    [Fact]
    public void NoLeader_NothingIsAsked()
    {
        using Harness h = new() { Leader = null };
        Assert.False(h.Probe.Ask());
        Assert.Empty(h.Probe.LastSentForTests);
    }

    [Fact]
    public void SomeoneElsesReply_IsIgnored()
    {
        using Harness h = new();
        h.Probe.Ask();

        h.Line("Suijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Empty(h.Answers);
    }

    [Fact]
    public void Recheck_AsksAgainOnlyWhileTheGearIsStillKept()
    {
        using Harness h = new();
        h.Probe.Ask();
        h.Line("Raijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 3/40}");
        Assert.Single(h.Probe.LastSentForTests);

        h.Probe.OnRecheck();
        Assert.Equal(2, h.Probe.LastSentForTests.Count);

        h.Probe.OnTimeout();
        h.StillNeeded = false;
        h.Probe.OnRecheck();
        Assert.Equal(2, h.Probe.LastSentForTests.Count);
    }
}
