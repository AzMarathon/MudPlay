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

// CharacterCalculator's per-melee-type matchup builder: UsableMeleeAttacks gates
// the attack list by class/race capability (mirroring
// CharacterInfoSectionViewModel.ComputeDerivedCombat), and BuildMeleeAttackProfile
// composes the per-type offense onto the shared defensive profile. The refactor
// that made BuildNormalAttackProfile delegate to BuildMeleeAttackProfile(Normal)
// must stay byte-identical, which the equality pin below locks down.
public sealed class MeleeMatchupProfilesTests : IDisposable
{
    private readonly string _root;

    public MeleeMatchupProfilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-melee-matchup-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // One isolated set carrying: a weapon, four classes (Warrior smash-capable,
    // Mage plain, Thief with class stealth, Mystic with all three MA strikes),
    // two races (Human plain, Halfling with race stealth), and a TBInfo chain
    // that restricts Smash to the Warrior (Number 1).
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
            new Dictionary<string, object> { ["Number"] = 1, ["Name"] = "Warrior", ["CombatLVL"] = 5 },
            new Dictionary<string, object> { ["Number"] = 2, ["Name"] = "Mage", ["CombatLVL"] = 3 },
            new Dictionary<string, object> { ["Number"] = 3, ["Name"] = "Thief", ["CombatLVL"] = 4, ["Abil-0"] = 103 },
            new Dictionary<string, object>
            {
                ["Number"] = 4, ["Name"] = "Mystic", ["CombatLVL"] = 4,
                ["Abil-0"] = 29, ["Abil-1"] = 30, ["Abil-2"] = 35,
            },
        }));
        File.WriteAllText(Path.Combine(dir, "Races.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Number"] = 1, ["Name"] = "Human" },
            new Dictionary<string, object> { ["Number"] = 2, ["Name"] = "Halfling", ["Abil-0"] = 102 },
        }));
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Action"] = "class 1:giveability 32 1" },
        }));

        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static PlayerStats Stats(string cls, string race) => new()
    {
        Name = "Tester", Class = cls, Race = race, Level = 20,
        Strength = 60, Agility = 60, Intellect = 50, Charm = 50, Stealth = 40, ArmourClass = 10,
    };

    private static readonly EncumbranceReading NoEncum = new(0, 0, 0, EncumbranceLevel.None);

    [Fact]
    public void BuildNormalAttackProfile_EqualsBuildMeleeAttackProfileNormal()
    {
        GameDataCache cache = NewCache();
        var worn = new[] { new EquippedItem("iron sword", "Weapon Hand") };
        PlayerStats stats = Stats("Warrior", "Human");

        PlayerMatchupProfile viaNormal = CharacterCalculator.BuildNormalAttackProfile(stats, worn, NoEncum, cache);
        PlayerMatchupProfile viaGeneric = CharacterCalculator.BuildMeleeAttackProfile(
            MudAttackType.Normal, stats, worn, NoEncum, cache);

        Assert.Equal(viaNormal, viaGeneric);
    }

    [Fact]
    public void UsableMeleeAttacks_AlwaysIncludesNormalAndBash()
    {
        GameDataCache cache = NewCache();

        foreach ((string cls, string race) in new[] { ("Warrior", "Human"), ("Mage", "Human"), ("Mystic", "Human") })
        {
            IReadOnlyList<MudAttackType> attacks = CharacterCalculator.UsableMeleeAttacks(Stats(cls, race), cache);
            Assert.Contains(MudAttackType.Normal, attacks);
            Assert.Contains(MudAttackType.Bash, attacks);
        }
    }

    [Fact]
    public void UsableMeleeAttacks_SmashOnlyForSmashCapableClass()
    {
        GameDataCache cache = NewCache();

        // Warrior (Number 1) is the only class the TBInfo chain restricts Smash to.
        Assert.Contains(MudAttackType.Smash, CharacterCalculator.UsableMeleeAttacks(Stats("Warrior", "Human"), cache));
        Assert.DoesNotContain(MudAttackType.Smash, CharacterCalculator.UsableMeleeAttacks(Stats("Mage", "Human"), cache));
    }

    [Fact]
    public void UsableMeleeAttacks_BackstabOnlyWithStealthSource()
    {
        GameDataCache cache = NewCache();

        // Class stealth (Thief, Abil 103) and race stealth (Halfling, Abil 102)
        // each unlock backstab; a plain Warrior/Human has neither.
        Assert.Contains(MudAttackType.Backstab, CharacterCalculator.UsableMeleeAttacks(Stats("Thief", "Human"), cache));
        Assert.Contains(MudAttackType.Backstab, CharacterCalculator.UsableMeleeAttacks(Stats("Warrior", "Halfling"), cache));
        Assert.DoesNotContain(MudAttackType.Backstab, CharacterCalculator.UsableMeleeAttacks(Stats("Warrior", "Human"), cache));
    }

    [Fact]
    public void UsableMeleeAttacks_MartialArtsOnlyForClassWithStrikes()
    {
        GameDataCache cache = NewCache();

        IReadOnlyList<MudAttackType> mystic = CharacterCalculator.UsableMeleeAttacks(Stats("Mystic", "Human"), cache);
        Assert.Contains(MudAttackType.Punch, mystic);
        Assert.Contains(MudAttackType.Kick, mystic);
        Assert.Contains(MudAttackType.Jumpkick, mystic);

        IReadOnlyList<MudAttackType> warrior = CharacterCalculator.UsableMeleeAttacks(Stats("Warrior", "Human"), cache);
        Assert.DoesNotContain(MudAttackType.Punch, warrior);
        Assert.DoesNotContain(MudAttackType.Kick, warrior);
        Assert.DoesNotContain(MudAttackType.Jumpkick, warrior);
    }

    [Fact]
    public void SmashProfile_LocksToSingleSwingWithNoCrit()
    {
        GameDataCache cache = NewCache();
        var worn = new[] { new EquippedItem("iron sword", "Weapon Hand") };

        PlayerMatchupProfile smash = CharacterCalculator.BuildMeleeAttackProfile(
            MudAttackType.Smash, Stats("Warrior", "Human"), worn, NoEncum, cache);

        Assert.Equal(1.0, smash.SwingsPerRound);
        Assert.Equal(0, smash.CritChancePercent);
    }
}
