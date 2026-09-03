using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// The Monster Intel "Apply Debuffs" what-if: MonsterDebuffCalculator folds the
// selected debuffs' stat magnitudes (allowing the monster's stat to be pushed
// negative), and MonsterMatchupCalculator derives the monster's swings/round
// from energy/attEnergy so a slowness debuff (raised attEnergy) thins them.
public sealed class MonsterDebuffCalculatorTests
{
    private static KnownSpell Debuff(string code, params SpellAbility[] abilities)
        => new(Number: 1, Short: code, Name: code, Magery: 0, MageryLvl: 0,
               ReqLevel: 1, Targets: 4, Formula: new SpellFormulaInput { EnergyCost = 0, Abilities = abilities });

    [Fact]
    public void Fold_EmptyOrNull_IsIdentity()
    {
        Assert.Equal(default, MonsterDebuffCalculator.Fold(null, 1));
        Assert.Equal(default, MonsterDebuffCalculator.Fold(new KnownSpell[0], 1));
    }

    [Fact]
    public void Fold_SubtractsMagnitudes_DrScaledByTen()
    {
        // Debuff values are stored signed ("-20 AC"); the fold takes the absolute
        // magnitude to subtract, and DR is stored at 10x (÷10 to damage points).
        MonsterDebuffEffect e = MonsterDebuffCalculator.Fold(new[]
        {
            Debuff("acdb", new SpellAbility(2, -20)),
            Debuff("drdb", new SpellAbility(7, -200)),
            Debuff("dodb", new SpellAbility(34, -15)),
            Debuff("acc", new SpellAbility(22, -10)),
        }, level: 10);

        Assert.Equal(20, e.AcDelta);
        Assert.Equal(20.0, e.DrDelta);   // -200 / 10
        Assert.Equal(15, e.DodgeDelta);
        Assert.Equal(10, e.AccDelta);
        Assert.False(e.Slowed);
    }

    [Fact]
    public void Fold_Slowness_SetsFlagNotMagnitude()
    {
        MonsterDebuffEffect e = MonsterDebuffCalculator.Fold(
            new[] { Debuff("slow", new SpellAbility(68, -5)) }, level: 1);
        Assert.True(e.Slowed);
        Assert.Equal(0, e.AcDelta);
    }

    [Fact]
    public void Fold_SumsAcross_MultipleDebuffs()
    {
        MonsterDebuffEffect e = MonsterDebuffCalculator.Fold(new[]
        {
            Debuff("a", new SpellAbility(2, -10)),
            Debuff("b", new SpellAbility(2, -15), new SpellAbility(68, -5)),
        }, level: 1);
        Assert.Equal(25, e.AcDelta);
        Assert.True(e.Slowed);
    }

    [Fact]
    public void AffectsMonsterStats_TrueForStatCode_FalseForCrowdControl()
    {
        Assert.True(MonsterDebuffCalculator.AffectsMonsterStats(Debuff("ac", new SpellAbility(2, -20))));
        Assert.True(MonsterDebuffCalculator.AffectsMonsterStats(Debuff("sl", new SpellAbility(68, -5))));
        // Confusion (71) / hold (74) are crowd control — no stat delta to fold.
        Assert.False(MonsterDebuffCalculator.AffectsMonsterStats(Debuff("cc", new SpellAbility(71, 0))));
        Assert.False(MonsterDebuffCalculator.AffectsMonsterStats(Debuff("hd", new SpellAbility(74, 0))));
    }

    [Theory]
    [InlineData(1000, 200, 5)]   // 1000 / 200
    [InlineData(1000, 300, 3)]   // a slowed 200-energy attack (×1.5 → 300)
    [InlineData(1200, 200, 6)]
    [InlineData(1000, 0, 1)]     // missing per-attack energy → one swing
    [InlineData(0, 200, 1)]      // missing budget → one swing
    public void MonsterSwingsPerRound_IsEnergyOverAttEnergy(int energy, int attEnergy, int expected)
        => Assert.Equal(expected, MonsterMatchupCalculator.MonsterSwingsPerRound(energy, attEnergy));

    [Fact]
    public void Compute_FoldsMonsterSwingsIntoDps()
    {
        var player = new PlayerMatchupProfile(
            Realm: RealmType.Stock, NormalAccuracy: 100, AvgWeaponDamage: 50, SwingsPerRound: 1,
            HasWeapon: true, ArmourClass: 0, Dodge: 0, ProtEvil: 0, ProtGood: 0, DamageResist: 0);
        var monster = new MonsterMatchupProfile(
            ArmourClass: 0, DamageResist: 0, Hp: 500, Dodge: 0, HasPhysicalAttack: true,
            AttackAccuracy: 100, AvgAttackDamage: 10, IsEvil: false, IsGood: false,
            EnergyPerRound: 1000, PrimaryAttackEnergy: 200);

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(player, monster);

        Assert.Equal(5, r.MonsterSwingsPerRound);
        // DPS folds swings: hit% * dmg/hit * swings (10 dmg × 5 swings at 100% base).
        Assert.True(r.MonsterDps >= r.MonsterDamagePerHit, "monster DPS should scale by its swing count");
    }
}
