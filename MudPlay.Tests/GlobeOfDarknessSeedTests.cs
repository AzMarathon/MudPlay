using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Globe of darkness against the shipped message records. It takes the light away
// and is no blindness (no Blindness ability, and cure blindness doesn't lift it),
// but its applied line is also blind globe's, which is one. A nanati shadow's
// globe set Blinded and had the party asked for a cure.
public sealed class GlobeOfDarknessSeedTests : IDisposable
{
    private readonly string _dir;

    public GlobeOfDarknessSeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-globe-seed-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private ConditionTracker TrackerOver(string seedFile, out List<MessageRecord> applied)
    {
        var store = new MessageStore();
        foreach (MessageRecord r in JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, seedFile)) ?? new())
            store.Messages.Add(r);
        var tracker = new ConditionTracker(store);
        List<MessageRecord> seen = new();
        tracker.ConditionApplied += seen.Add;
        applied = seen;
        return tracker;
    }

    private static void Feed(ConditionTracker tracker, string text) =>
        typeof(ConditionTracker)
            .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tracker, new object[]
            {
                new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false),
            });

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void TheShadowsGlobe_IsNotABlindness(string seedFile)
    {
        using ConditionTracker tracker = TrackerOver(seedFile, out List<MessageRecord> applied);

        Feed(tracker, "The thin nanati shadow makes a clutching gesture!");
        Feed(tracker, "A bubble of pure darkness appears and begins to expand!");
        Feed(tracker, "You are enveloped in darkness!");

        Assert.False(tracker.IsBlinded);
        Assert.Contains(applied, r => r.Name == "globe of darkness");
        Assert.DoesNotContain(applied, r => r.Name == "blind globe");

        Feed(tracker, "The unnatural darkness lifts.");
        Assert.DoesNotContain(applied, tracker.IsActive);
    }

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void ABlindGlobe_WhichPrintsNoCastLine_StillBlinds(string seedFile)
    {
        using ConditionTracker tracker = TrackerOver(seedFile, out _);

        Feed(tracker, "You are enveloped in darkness!");

        Assert.True(tracker.IsBlinded);
    }
}
