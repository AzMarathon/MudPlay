using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Tokens;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The token use lines read from the shipped Paradigm message records, through the
// token tracker as the client wires it. Our own use prints a line that names no one,
// which the cast-line matcher makes nothing of, so no use of ours was recognized:
// the buff hold sat out its timeout and nothing heard of the teleport (report
// paradigm-20261022-164244, a token of Arlysia).
public sealed class TokenUseLinesTests : IDisposable
{
    private readonly ServerFeed _feed = new();
    private readonly string _dir;
    private readonly MessageStore _store = new();
    private readonly TokenTracker _tracker;
    private readonly ManualTimers _timers = new();
    private readonly List<string> _used = new();
    private readonly List<string> _departed = new();

    public TokenUseLinesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-token-lines-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        foreach (MessageRecord r in JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.paradigm.seed.json")) ?? new())
            _store.Messages.Add(r);
        TokenUseLines lines = new(_store);
        _tracker = new TokenTracker(
            send: _ => { },
            carried: () => new[] { "token of Arlysia" },
            onParadigm: () => true,
            matchSelfUse: lines.OwnUse,
            matchMemberDeparted: lines.WitnessedUse,
            schedule: (ms, tick) => _timers.Schedule(TimeSpan.FromMilliseconds(ms), tick));
        _tracker.AttachLineExtractor(_feed.Lines);
        _tracker.TokenUsed += _used.Add;
        _tracker.MemberDeparted += _departed.Add;
    }

    public void Dispose()
    {
        _tracker.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    [Fact]
    public void OurOwnUse_IsRecognized_AndLetsBuffingResume()
    {
        _tracker.ObserveOutbound(Encoding.Latin1.GetBytes("use arly\r\n"));
        Assert.True(_tracker.IsBuffPausedForToken);

        _feed.Line("The effects of blur wear off.");
        _feed.Line("Arlysia, City Entrance");
        _feed.Line("You invoke the token and summon a gryphon to Arlysia!");

        Assert.Equal(new[] { "Arlysia" }, _used);
        Assert.False(_tracker.IsBuffPausedForToken);
    }

    [Fact]
    public void SomeoneElsesUse_NamesThem_AndIsNotOurs()
    {
        _feed.Line("Curdie invokes a token and summons a gryphon to Arlysia!");

        Assert.Equal(new[] { "Curdie" }, _departed);
        Assert.Empty(_used);
    }

    // Our line is matched whole: quoted in a chat line it is no use of ours.
    [Fact]
    public void OurLineQuotedInChat_IsNotAUse()
    {
        _feed.Line("Curdie says \"You invoke the token and summon a gryphon to Arlysia!\"");

        Assert.Empty(_used);
    }

    // The lines follow the catalogue: a record edited in place is read afresh.
    [Fact]
    public void AnEditedRecord_IsReadAfresh()
    {
        int i = _store.Messages.IndexOf(Find("token of Arlysia"));
        _store.Messages[i] = _store.Messages[i] with { CasterMessage = "The gryphon carries you off to Arlysia!" };

        _feed.Line("You invoke the token and summon a gryphon to Arlysia!");
        _feed.Line("The gryphon carries you off to Arlysia!");

        Assert.Equal(new[] { "Arlysia" }, _used);
    }

    private MessageRecord Find(string name)
    {
        foreach (MessageRecord r in _store.Messages)
            if (r.Name == name) return r;
        throw new InvalidOperationException(name);
    }
}
