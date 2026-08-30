using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public class WhereReplyTrackerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    [Fact]
    public void RecognisedReply_AnnouncesSenderAndRoom()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        using WhereReplyTracker tracker = new(router);

        (string Name, RoomKey Room)? got = null;
        tracker.TargetLocated += (name, room) => got = (name, room);

        router.Dispatch(Line("Fujin telepaths: {Adventurer's Guild, Universal Trainer (map 1, room 1376); exit s: west}"));

        Assert.NotNull(got);
        Assert.Equal("Fujin", got!.Value.Name);
        Assert.Equal(new RoomKey(1, 1376), got.Value.Room);
    }

    [Fact]
    public void OrdinaryTelepath_IsIgnored()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        using WhereReplyTracker tracker = new(router);

        bool fired = false;
        tracker.TargetLocated += (_, _) => fired = true;

        router.Dispatch(Line("Fujin telepaths: heading to map 1, room 1376 now"));

        Assert.False(fired);
    }
}
