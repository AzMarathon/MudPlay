using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// FollowModeTracker: the `set follow` mode read off the `pro` sheet's row and the
// command's two replies, with one notice when it turns up Blind.
public sealed class FollowModeTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public FollowModeTracker Tracker { get; }
        public int Notices { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Tracker = new FollowModeTracker(Router, Log);
            Tracker.BlindNoticed += () => Notices++;
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        public int Warnings => Log.Snapshot().Count(e => e.Severity == LogSeverity.Warn);

        public void Dispose() => Tracker.Dispose();
    }

    [Fact]
    public void UnknownUntilTheGameSays()
    {
        using Harness h = new();
        Assert.Equal(FollowMode.Unknown, h.Tracker.Mode);
        Assert.Equal(0, h.Notices);
    }

    [Fact]
    public void ProfileRow_Normal_IsReadQuietly()
    {
        using Harness h = new();
        h.Feed("Follow Mode:        Normal");
        Assert.Equal(FollowMode.Normal, h.Tracker.Mode);
        Assert.Equal(0, h.Notices);
        Assert.Equal(0, h.Warnings);
    }

    // A sheet looked at again repeats the row; the user is told once.
    [Fact]
    public void ProfileRow_Blind_NoticedOnce_HoweverOftenTheSheetIsRead()
    {
        using Harness h = new();
        h.Feed("Follow Mode:        Blind");
        h.Feed("Follow Mode:        Blind");
        h.Feed("Follow Mode:        Blind");

        Assert.Equal(FollowMode.Blind, h.Tracker.Mode);
        Assert.Equal(1, h.Notices);
        Assert.Equal(1, h.Warnings);
    }

    [Fact]
    public void SetFollowReplies_SwitchTheMode_AndBlindIsNoticedEachTimeItIsTurnedOn()
    {
        using Harness h = new();
        h.Feed("You will only see the fact that you have moved when following.");
        Assert.Equal(FollowMode.Blind, h.Tracker.Mode);
        Assert.Equal(1, h.Notices);

        h.Feed("You will see your normal room descriptions when following.");
        Assert.Equal(FollowMode.Normal, h.Tracker.Mode);

        h.Feed("You will only see the fact that you have moved when following.");
        Assert.Equal(2, h.Notices);
    }

    // Another character's sheet starts from nothing.
    [Fact]
    public void Reset_ForgetsTheModeAndTheNotice()
    {
        using Harness h = new();
        h.Feed("Follow Mode:        Blind");
        h.Tracker.Reset();
        Assert.Equal(FollowMode.Unknown, h.Tracker.Mode);

        h.Feed("Follow Mode:        Blind");
        Assert.Equal(2, h.Notices);
    }
}
