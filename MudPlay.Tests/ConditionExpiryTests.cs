using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// An ailment of ours whose wear-off line never comes ends once its spell's time is
// up (Client policy, user 2026-10-23). Confused was held for 23 minutes on spells of
// seconds and the walk to Volum's sat paused on it (report paradigm-20261022-164244).
// Against the shipped Paradigm records, the game's lines fed the way the client gets
// them, each at its own time.
public sealed class ConditionExpiryTests : IDisposable
{
    private readonly ServerFeed _feed = new();
    private readonly string _dir;
    private readonly LogService _log = new();
    private readonly ConditionTracker _tracker;
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 16, 19, 40, TimeSpan.FromHours(-4));

    public ConditionExpiryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-condition-expiry-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        MessageStore store = new();
        foreach (MessageRecord r in JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.paradigm.seed.json")) ?? new())
            store.Messages.Add(r);
        _tracker = new ConditionTracker(store, _log);
        _tracker.AttachLineExtractor(_feed.Lines);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private void At(double seconds, string? line = null)
    {
        if (line is null) _feed.Prompt(at: T0.AddSeconds(seconds));
        else _feed.Line(line, T0.AddSeconds(seconds));
    }

    [Fact]
    public void AnAilmentWithNoWearOff_EndsWhenItsSpellsTimeIsUp_AndSaysSo()
    {
        _tracker.AilmentSeconds = r => r.Name == "banish" ? 61 : null;

        At(0, "You are afflicted by teleportation sickness!");
        Assert.True(_tracker.IsConfused);
        Assert.True(_tracker.IsPoisoned);

        At(60);
        Assert.True(_tracker.IsConfused);
        At(62);

        Assert.False(_tracker.IsConfused);
        Assert.False(_tracker.IsPoisoned);
        Assert.Empty(_tracker.ActiveRecords());
        Assert.Contains(_log.Snapshot(), e => e.Severity == LogSeverity.Info && e.Source == ConditionTracker.LogCategory
            && e.Message.StartsWith("condition expired name='banish'", StringComparison.Ordinal));
    }

    // Its applied line seen again is the ailment cast anew: its time starts over.
    [Fact]
    public void TheAppliedLineAgain_StartsItsTimeOver()
    {
        _tracker.AilmentSeconds = _ => 61;

        At(0, "You are afflicted by teleportation sickness!");
        At(50, "You are afflicted by teleportation sickness!");
        At(70);
        Assert.True(_tracker.IsConfused);

        At(112);
        Assert.False(_tracker.IsConfused);
    }

    // No duration in the data: the cap the party's chips use.
    [Fact]
    public void WithNoDurationInTheData_ThePartyChipsCapApplies()
    {
        _tracker.AilmentSeconds = _ => null;

        At(0, "You are afflicted by teleportation sickness!");
        At(PartyAilmentTracker.FallbackDurationSeconds - 1);
        Assert.True(_tracker.IsConfused);
        (_, _, _, DateTimeOffset? expires) = Assert.Single(_tracker.ActiveRecords());
        Assert.Equal(T0.AddSeconds(PartyAilmentTracker.FallbackDurationSeconds), expires);

        At(PartyAilmentTracker.FallbackDurationSeconds + 1);
        Assert.False(_tracker.IsConfused);
    }

    // A buff is no ailment: its own timers end it, never this.
    [Fact]
    public void ABuff_IsNeverExpiredHere()
    {
        _tracker.AilmentSeconds = _ => 1;

        At(0, "You feel protected!");
        Assert.NotEmpty(_tracker.ActiveRecords());
        At(600);

        Assert.NotEmpty(_tracker.ActiveRecords());
    }
}
