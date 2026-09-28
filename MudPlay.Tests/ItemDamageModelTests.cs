using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ItemDamageModel feeds the Item Finder's Est. BS Dmg / Dmg/Rnd columns and the
// damage-based Find Best criteria. These pin that it routes through CombatCalculator's
// existing formulas with the right inputs: rest-of-gear bonuses folded in, a weapon's
// own stat bonuses swapped for the wielded one's (not stacked), and gear scored by
// what it adds.
public sealed class ItemDamageModelTests : IDisposable
{
    private readonly string _root;

    public ItemDamageModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-dmgmodel-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static readonly ItemDamageModel.RestBonuses Rest = new(
        PlusMin: 1, PlusMax: 3, Crits: 0, BsMin: 2, BsMax: 4, PunchDmg: 0, KickDmg: 0, JumpKickDmg: 0);

    private static ItemDamageModel Model(ItemDamageModel.WeaponInputs? current = null) => new(
        RealmType.ParaMud, Level: 20, CombatLevel: 3, Strength: 70, Agility: 60, Stealth: 80,
        HasClassStealth: true, CurrentEncum: 0, MaxEncum: 1000, Rest, current);

    private static readonly ItemDamageModel.WeaponInputs Stiletto = new(
        Min: 4, Max: 12, Speed: 1100, StrReq: 10, PlusMin: 0, PlusMax: 1, Crits: 0, BsMin: 5, BsMax: 6,
        CanBackstab: true);

    [Fact]
    public void Backstab_UsesCalcBSDamageWithRestAndWeaponBonuses()
    {
        BSDamageResult? bs = Model().Backstab(Stiletto);

        BSDamageResult expected = CombatCalculator.CalcBSDamage(
            20, 80, 70, 4, 12, bsMinBonus: 2 + 5, bsMaxBonus: 4 + 6, maxDmgBonus: 3 + 1,
            hasClassStealth: true, RealmType.ParaMud, minDmgBonus: 1);
        Assert.Equal(expected, bs);
    }

    [Fact]
    public void Backstab_WeaponWithoutBackstabAbility_IsNull()
    {
        Assert.Null(Model().Backstab(Stiletto with { CanBackstab = false }));
        Assert.Equal(0, Model().DamagePerRound(Stiletto with { CanBackstab = false }, MudAttackType.Backstab));
    }

    [Fact]
    public void Backstab_SwapsTheWieldedWeaponsStealthOut()
    {
        // The live Stealth 80 includes the wielded weapon's +10; a candidate without
        // it must be scored at 70, not 80.
        var wielded = Stiletto with { Stealth = 10 };
        BSDamageResult? bs = Model(wielded).Backstab(Stiletto);

        BSDamageResult expected = CombatCalculator.CalcBSDamage(
            20, 70, 70, 4, 12, 7, 10, 4, hasClassStealth: true, RealmType.ParaMud, minDmgBonus: 1);
        Assert.Equal(expected, bs);
    }

    [Fact]
    public void DamagePerRound_Backstab_IsOneStrikeAtTheAverage()
    {
        ItemDamageModel m = Model();
        Assert.Equal(m.Backstab(Stiletto)!.Value.AvgDamage, m.DamagePerRound(Stiletto, MudAttackType.Backstab));
    }

    [Fact]
    public void DamagePerRound_Normal_IsSwingsTimesCritBlendedHit()
    {
        MeleeOffense o = CombatCalculator.ComputeMeleeOffense(
            MudAttackType.Normal, RealmType.ParaMud, 20, 3, 70, 60, 4, 12, 1100, 10,
            plusMaxDamage: 3 + 1, plusMinDamage: 1, plusCrits: 0, currentEncum: 0, maxEncum: 1000);
        double crit = o.CritChance / 100.0;
        double expected = o.SwingsPerRound * ((1 - crit) * o.AvgDamage + crit * o.AvgCritDamage);

        Assert.True(expected > 0);
        Assert.Equal(expected, Model().DamagePerRound(Stiletto, MudAttackType.Normal), 6);
    }

    [Fact]
    public void BackstabGain_BsMaxRing_RaisesMaxOnly()
    {
        var ring = new ItemDamageModel.GearDelta(0, 0, 0, 0, 0, 0, BsMin: 0, BsMax: 10, 0, 0, 0);
        (double min, double max, double avg) = Model(Stiletto).BackstabGain(ring);

        Assert.Equal(0, min);
        Assert.True(max > 0);
        Assert.Equal(max / 2, avg);
    }

    [Fact]
    public void Gains_ItemWithNothingDamageRelated_ScoresZero()
    {
        ItemDamageModel m = Model(Stiletto);
        Assert.Equal((0.0, 0.0, 0.0), m.BackstabGain(default));
        Assert.Equal(0, m.DamagePerRoundGain(default, MudAttackType.Normal));
    }

    [Fact]
    public void NotUsable_WithoutLevelOrCombatLevel_YieldsNothing()
    {
        ItemDamageModel noLevel = Model() with { Level = 0 };
        Assert.False(noLevel.IsUsable);
        Assert.Null(noLevel.Backstab(Stiletto));
        Assert.Equal(0, noLevel.DamagePerRound(Stiletto, MudAttackType.Normal));
    }

    // ----- through BuildCatalog -----

    // - bs dagger : 1H weapon with the backstab ability (116).
    // - club      : 1H weapon, no backstab ability.
    // - shadow ring: finger armour with +BS max (118) and +stealth (27).
    private const string Items =
        "[{\"Number\":1,\"Name\":\"bs dagger\",\"ItemType\":1,\"WeaponType\":2,\"Speed\":1100,\"StrReq\":0,\"Min\":4,\"Max\":12,\"Abil-0\":116,\"AbilVal-0\":1,\"In Game\":1}," +
        " {\"Number\":2,\"Name\":\"club\",\"ItemType\":1,\"WeaponType\":0,\"Speed\":1100,\"StrReq\":0,\"Min\":4,\"Max\":12,\"In Game\":1}," +
        " {\"Number\":3,\"Name\":\"shadow ring\",\"ItemType\":0,\"Worn\":4,\"Abil-0\":118,\"AbilVal-0\":10,\"Abil-1\":27,\"AbilVal-1\":20,\"In Game\":1}]";

    private GameDataCache SeededCache()
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), Items);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        return cache;
    }

    private static ItemFinderEntry.SwingContext Swing() => new(
        CombatLevel: 3, Level: 20, Agility: 60, Strength: 70, CurrentEncum: 0, MaxEncum: 1000, Realm: RealmType.ParaMud);

    [Fact]
    public void Catalog_BackstabAttackType_OneSwingAndBsDamageOnCapableWeaponsOnly()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(
            SeededCache(), Swing(), MudAttackType.Backstab, Model());

        ItemFinderEntry dagger = catalog.Single(e => e.Name == "bs dagger");
        ItemFinderEntry club = catalog.Single(e => e.Name == "club");

        Assert.Equal(1, dagger.AvgSwings);
        Assert.True(dagger.EstBsMax > 0);
        Assert.Equal((dagger.EstBsMin + dagger.EstBsMax) / 2.0, dagger.DamagePerRound);
        Assert.Equal(dagger.DamagePerRound, dagger.DamagePerRoundScore);

        Assert.Equal(0, club.AvgSwings);
        Assert.Equal(string.Empty, club.EstBsText);
        Assert.Equal(string.Empty, club.DamagePerRoundText);
    }

    [Fact]
    public void Catalog_Gear_ScoresWhatItAddsToTheBackstab()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(
            SeededCache(), Swing(), MudAttackType.Normal, Model(Stiletto));

        ItemFinderEntry ring = catalog.Single(e => e.Name == "shadow ring");
        (double min, double max, double avg) = Model(Stiletto).BackstabGain(
            new ItemDamageModel.GearDelta(0, 0, Stealth: 20, 0, 0, 0, 0, BsMax: 10, 0, 0, 0));

        Assert.Equal(min, ring.BsScoreMin);
        Assert.Equal(max, ring.BsScoreMax);
        Assert.Equal(avg, ring.BsScoreAvg);
        Assert.True(ring.BsScoreMax > ring.BsScoreMin);
        // Gear shows no weapon-only columns of its own.
        Assert.Equal(string.Empty, ring.EstBsText);
    }
}
