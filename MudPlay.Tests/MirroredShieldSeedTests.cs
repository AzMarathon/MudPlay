using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Mirrored shield (#169, Stock) against the shipped Stock message records. The
// shield's use casts a room-wide blindness (BlindUser) whose description message is
// speed's (DescMsg 8537 in the Spells data), so the Stock engine shows it landing as
// `You feel fast!` and wearing off as `You slow down.` (wccmmud.dll 1.11p
// `_display_spell_success` prints the DescMsg's third line to the target, and
// `_perform_spell_termination_player_upkeep` its first). Its own flash line tells it
// from a speed cast.
public sealed class MirroredShieldSeedTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _dir;
    private readonly List<MessageRecord> _records;

    public MirroredShieldSeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-shield-seed-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        _records = JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.stock.seed.json")) ?? new();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private ConditionTracker Tracker()
    {
        var store = new MessageStore();
        foreach (MessageRecord r in _records) store.Messages.Add(r);
        var tracker = new ConditionTracker(store);
        tracker.AttachLineExtractor(_feed.Lines);
        return tracker;
    }

    [Fact]
    public void TheShieldsRecord_CarriesSpeedsDescriptionText_AndBlinds()
    {
        MessageRecord shield = Assert.Single(_records,
            r => r.Links?.Any(l => l.Table == "Spells" && l.Number == 169) == true);

        Assert.Equal("You feel fast", shield.AppliedMessage);
        Assert.Equal("You slow down", shield.AppliedEndsWith);
        Assert.Equal(MessageFlags.Blinded, shield.Flags);
    }

    // A speed cast on us prints its own line first: the shield's blindness, which
    // shares the applied line, is not read off it.
    [Fact]
    public void ASpeedCastOnUs_IsNotABlindness()
    {
        using ConditionTracker tracker = Tracker();

        _feed.Line("Kenwood casts speed on you.");
        _feed.Line("You feel fast!");

        Assert.False(tracker.IsBlinded);
    }

    [Fact]
    public void TheShieldsFlash_ThenTheSameLine_Blinds()
    {
        using ConditionTracker tracker = Tracker();

        _feed.Line("Kenwood's shield flashes blindingly!");
        _feed.Line("You feel fast!");

        Assert.True(tracker.IsBlinded);
    }
}
