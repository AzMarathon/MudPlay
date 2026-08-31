using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// CharacterCalculator.BuildNormalAttackProfile composes AggregateEquipmentStats
// + the CombatCalculator Normal-attack formulas into a PlayerMatchupProfile for
// MonsterMatchupCalculator.Compute -- the same recipe
// CalculatorsSectionViewModel.CaptureActuals/ComputeWeaponOffense use for their
// own Normal-attack case, now shared with Monster Intel's rounds-to-kill column.
public sealed class CharacterCalculatorMatchupProfileTests : IDisposable
{
    private readonly string _root;

    public CharacterCalculatorMatchupProfileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-matchup-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private GameDataCache NewCache()
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "iron sword", ["Min"] = 5, ["Max"] = 15, ["Speed"] = 1500, ["StrReq"] = 40,
            },
        }));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "Warrior", ["CombatLVL"] = 5 },
        }));
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static PlayerStats NewStats() => new()
    {
        Name = "Tester", Class = "Warrior", Race = "Human", Level = 20,
        Strength = 60, Agility = 60, Intellect = 50, Charm = 50, ArmourClass = 10,
    };

    [Fact]
    public void ArmedCharacter_ProjectsPositiveAccuracyDamageAndSwings()
    {
        GameDataCache cache = NewCache();
        var worn = new[] { new EquippedItem("iron sword", "Weapon Hand") };
        var encum = new EncumbranceReading(0, 0, 0, EncumbranceLevel.None);

        PlayerMatchupProfile profile = CharacterCalculator.BuildNormalAttackProfile(
            NewStats(), worn, encum, cache);

        Assert.True(profile.HasWeapon);
        Assert.True(profile.AvgWeaponDamage > 0);
        Assert.True(profile.SwingsPerRound > 0);
        Assert.True(profile.NormalAccuracy > 0);
    }

    [Fact]
    public void UnarmedCharacter_HasNoWeaponAndZeroDamage()
    {
        GameDataCache cache = NewCache();
        var encum = new EncumbranceReading(0, 0, 0, EncumbranceLevel.None);

        PlayerMatchupProfile profile = CharacterCalculator.BuildNormalAttackProfile(
            NewStats(), Array.Empty<EquippedItem>(), encum, cache);

        Assert.False(profile.HasWeapon);
        Assert.Equal(0, profile.AvgWeaponDamage);
        Assert.Equal(0, profile.SwingsPerRound);
    }

    [Fact]
    public void RoundsToKill_FeedsIntoMonsterMatchupCalculator()
    {
        GameDataCache cache = NewCache();
        var worn = new[] { new EquippedItem("iron sword", "Weapon Hand") };
        var encum = new EncumbranceReading(0, 0, 0, EncumbranceLevel.None);

        PlayerMatchupProfile playerProfile = CharacterCalculator.BuildNormalAttackProfile(
            NewStats(), worn, encum, cache);
        var monsterProfile = new MonsterMatchupProfile(
            ArmourClass: 5, DamageResist: 0, Hp: 30, Dodge: 0,
            HasPhysicalAttack: true, AttackAccuracy: 40, AvgAttackDamage: 3,
            IsEvil: false, IsGood: false);

        MonsterMatchupResult result = MonsterMatchupCalculator.Compute(playerProfile, monsterProfile);

        Assert.True(result.RoundsToKill > 0);
    }
}
