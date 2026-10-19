using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The training run's own CP step, driven through TrainInPlace on the
// ErrandLightWorld stack: after the level-up and the `stat` that confirms it, the
// run opens `train stats` when a plan row is due at the level it reached.
public sealed class TrainerWalkCpPlanTests : IDisposable
{
    private readonly ErrandLightWorld _w = new();

    public void Dispose() => _w.Dispose();

    // Report paradigm-20261018-221236: a plan ending at level 35 and a run reaching
    // 39. Only a row for exactly the reached level used to count, so the run
    // trained and never opened `train stats`.
    [Fact]
    public void ARunReachingPastThePlansLastRow_OpensTrainStats()
    {
        _w.Profile.Current!.CharacterPlan = new List<CpPlanEntry>
        {
            new(31, 134, 40, 30, 100, 60, 30),
            new(35, 136, 40, 30, 100, 60, 30),
        };
        _w.Profile.Current.Settings = new Dictionary<string, JsonElement>
        {
            ["AutoTrainer"] = JsonSerializer.SerializeToElement(
                new AutoTrainerSettings { AutoTrain = false, AutoTrainStats = true }),
        };
        TrainerWalkManager trainer = _w.NewTrainer(level: 38);

        trainer.TrainInPlace();
        Assert.Equal(new[] { "train" }, _w.TrainerSent);

        _w.Line("You hand over 112850 copper farthings to train to the next level!");
        _w.Line("Welcome to level 39!");
        _w.Line("You gain 25 CPs");
        _w.Line("[HP=318/KAI=14]:");
        Assert.Equal(new[] { "train", "stat" }, _w.TrainerSent);

        Stat(level: 39, cp: 227);

        Assert.Equal(new[] { "train", "stat", "train stats" }, _w.TrainerSent);
        Assert.True(trainer.IsBusy);
    }

    private void Stat(int level, int cp)
    {
        _w.Line($"Name: Someone Somewhere              Lives/CP:     9/{cp}");
        _w.Line("Race: Half-Orc    Exp: 125379954       Perception:     50");
        _w.Line($"Class: Mystic     Level: {level,-14}Stealth:       107");
        _w.Line("Hits:   318/321   Armour Class:  26/6  Thievery:        0");
        _w.Line("Strength:  133   Agility: 100        Tracking:        0");
        _w.Line("Intellect: 40     Health:  60          Martial Arts:   97");
        _w.Line("Willpower: 30     Charm:   30          MagicRes:       47");
        _w.Line("[HP=318/KAI=14]:");
    }
}
