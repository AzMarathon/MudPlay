using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PR D inbound @panic: a party member's bare "@panic" on say makes us bail via
// the respond callback (→ HealthManager.RespondToReceivedPanic) — unless
// PartySettings.IgnorePanics is set, the speaker isn't a party member, or it's
// our own echo.
public sealed class PanicResponderTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public ChatRouter Chat { get; }
        public PartyState State { get; } = new();
        public PartyManager Party { get; }
        public PartySettings Settings { get; set; } = new();
        public List<string> Responded { get; } = new();
        public PanicResponder Responder { get; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Party = new PartyManager(Router, State);
            Responder = new PanicResponder(
                Chat, State,
                readPartySettings: () => Settings,
                respond: who => Responded.Add(who));
        }

        // Add a party follower by replaying the follow signal.
        public void AddMember(string name) =>
            Router.Dispatch(Line($"{name} started to follow you."));

        // Dispatch a server line so ChatRouter classifies it.
        public void Say(string line) => Router.Dispatch(Line(line));

        public void Dispose() => Responder.Dispose();
    }

    [Fact]
    public void PartymatePanic_Default_TriggersResponse()
    {
        using Harness h = new();
        h.AddMember("Bob");

        h.Say(@"Bob says ""@panic""");

        Assert.Equal(new[] { "Bob" }, h.Responded);
    }

    [Fact]
    public void PartymatePanic_IgnorePanicsSet_DoesNotRespond()
    {
        using Harness h = new();
        h.AddMember("Bob");
        h.Settings = new PartySettings { IgnorePanics = true };

        h.Say(@"Bob says ""@panic""");

        Assert.Empty(h.Responded);
    }

    [Fact]
    public void NonPartyStranger_DoesNotRespond()
    {
        using Harness h = new();
        h.AddMember("Bob");   // we ARE in a party, but the panicker isn't in it

        h.Say(@"Stranger says ""@panic""");

        Assert.Empty(h.Responded);
    }

    [Fact]
    public void NotInParty_DoesNotRespond()
    {
        using Harness h = new();   // no members → IsInParty false

        h.Say(@"Bob says ""@panic""");

        Assert.Empty(h.Responded);
    }

    [Fact]
    public void OwnEcho_NullSpeaker_DoesNotRespond()
    {
        using Harness h = new();
        h.AddMember("Bob");

        // Our own broadcast echoes as "You say" with a null speaker.
        h.Say(@"You say ""@panic""");

        Assert.Empty(h.Responded);
    }

    [Fact]
    public void PanicOnTelepath_NotSay_DoesNotRespond()
    {
        using Harness h = new();
        h.AddMember("Bob");

        // @panic rides the say channel only; a telepath carrying the word is not it.
        h.Say(@"Bob telepaths: ""@panic""");

        Assert.Empty(h.Responded);
    }

    [Fact]
    public void GivenNameMatch_FamilyNamedMember_StillResponds()
    {
        using Harness h = new();
        // Roster row carries a family name (as a par table yields); chat names the
        // given name only — the membership check must match on the given name.
        h.State.Members.Add(new PartyMember { Name = "Bob Bobson" });
        h.State.IsInParty = true;

        h.Say(@"Bob says ""@panic""");

        Assert.Equal(new[] { "Bob" }, h.Responded);
    }
}
