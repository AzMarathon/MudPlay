using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A follower tops off when the party stops to rest: the leader resting or
// meditating, or another member doing so with no follow drag since it was seen.
// Every line goes through ServerFeed and the router into a real PartyManager.
public sealed class PartyRestWatcherTests
{
    private sealed class Harness : IDisposable
    {
        private readonly ServerFeed _server;
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public PartyRestWatcher Watcher { get; }
        public int Nudges { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            _server = new ServerFeed(Router);
            Manager = new PartyManager(Router, Party) { LocalCharacterName = "Fujin" };
            Manager.AttachLineExtractor(_server.Lines);
            Watcher = new PartyRestWatcher(Party, Manager, Router, () => Nudges++);
        }

        public void Line(string text) => _server.Line(text);

        // A follower's `par`: the follow line, the header, the rows, our own row
        // last, and the prompt that ends it.
        public void ParReply(params string[] rows)
        {
            Line("You are following Arax.");
            Line("The following people are in your travel party:");
            foreach (string row in rows) Line(row);
            Line(Row("Fujin"));
            Line("[HP=100/MA=50]:");
        }

        public void Drag() => Line(" -- Following your Party leader east --");

        public void Dispose()
        {
            Watcher.Dispose();
            Manager.Dispose();
        }
    }

    // A par row; state is the R / M letter or a space.
    private static string Row(string name, char state = ' ') =>
        $"  {name,-30} {"(Priest)",-12} [M:100%] [H:100%] {state} - Midrank";

    private static Harness Following()
    {
        Harness h = new();
        h.Line("You are now following Arax.");
        return h;
    }

    [Fact]
    public void MemberSeenResting_WithNoDragSince_PartyRests()
    {
        using Harness h = Following();
        h.ParReply(Row("Arax"), Row("Raijin", 'M'));

        Assert.Equal("Raijin is meditating", h.Watcher.PartyRestingFor);
        Assert.Equal(1, h.Nudges);
    }

    [Fact]
    public void Drag_ForgetsTheMembersRest_UntilParListsItAgain()
    {
        using Harness h = Following();
        h.ParReply(Row("Arax"), Row("Raijin", 'R'));
        Assert.Equal("Raijin is resting", h.Watcher.PartyRestingFor);

        h.Drag();
        Assert.Null(h.Watcher.PartyRestingFor);
        Assert.Equal(2, h.Nudges);

        // The leader stopped again and the next par still has Raijin resting.
        h.ParReply(Row("Arax"), Row("Raijin", 'R'));
        Assert.Equal("Raijin is resting", h.Watcher.PartyRestingFor);
        Assert.Equal(3, h.Nudges);
    }

    [Fact]
    public void MemberStopsToRest_AfterADrag_CountsAtOnce()
    {
        using Harness h = Following();
        h.ParReply(Row("Arax"), Row("Raijin"));
        h.Drag();

        h.Line("Raijin stops to rest.");

        Assert.Equal("Raijin is resting", h.Watcher.PartyRestingFor);
    }

    [Fact]
    public void LeaderResting_CountsWhateverTheDrags()
    {
        using Harness h = Following();
        h.Drag();
        h.ParReply(Row("Arax", 'R'), Row("Raijin"));

        Assert.Equal("the leader is resting", h.Watcher.PartyRestingFor);
    }

    [Fact]
    public void OurOwnRest_DoesNotCount()
    {
        using Harness h = Following();
        h.Line("You are following Arax.");
        h.Line("The following people are in your travel party:");
        h.Line(Row("Arax"));
        h.Line(Row("Fujin", 'R'));
        h.Line("[HP=100/MA=50]:");

        Assert.Null(h.Watcher.PartyRestingFor);
        Assert.Equal(0, h.Nudges);
    }

    [Fact]
    public void Leading_AMemberResting_DoesNotCount()
    {
        using Harness h = new();
        h.Line("Raijin started to follow you.");
        h.Line("The following people are in your travel party:");
        h.Line(Row("Raijin", 'R'));
        h.Line(Row("Fujin"));
        h.Line("[HP=100/MA=50]:");

        Assert.True(h.Party.SelfIsLeader);
        Assert.Null(h.Watcher.PartyRestingFor);
    }
}
