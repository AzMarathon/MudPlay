using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Evil-only (ability 98) gates on evil points: value 0 needs Outlaw through Fiend,
// value N needs at least N. The client knows a range — Paradigm's exact `pro`
// number, else the who title's band, narrowed by a refused evil-only item — and
// lets a straddling range through for the game to decide.
public sealed class EvilPointRangeTests
{
    [Theory]
    [InlineData("Seedy", RealmType.Stock, 30, 39)]
    [InlineData("Outlaw", RealmType.ParaMud, 40, 79)]
    [InlineData("Villain", RealmType.Stock, 120, 209)]
    [InlineData("Villain", RealmType.ParaMud, 120, 299)]
    [InlineData("fiend", RealmType.Stock, 210, double.PositiveInfinity)]
    [InlineData("Neutral", RealmType.ParaMud, -50, 29)]
    public void ForTitle_MatchesTheBandThresholds(string title, RealmType realm, double lo, double hi) =>
        Assert.Equal(new EvilPointRange(lo, hi), EvilPointRange.ForTitle(title, realm));

    [Fact]
    public void ForTitle_UnknownTitle_IsNull() => Assert.Null(EvilPointRange.ForTitle("Wanderer", RealmType.Stock));

    [Fact]
    public void EvilOnlyZero_NeedsOutlaw()
    {
        Assert.False(EvilPointRange.ForTitle("Seedy", RealmType.ParaMud)!.Value.MeetsEvilOnly(0));
        Assert.True(EvilPointRange.ForTitle("Outlaw", RealmType.ParaMud)!.Value.MeetsEvilOnly(0));
    }

    [Fact]
    public void EvilOnlyValue_IsMetOnlyWhenTheWholeRangeReachesIt()
    {
        EvilPointRange villain = EvilPointRange.ForTitle("Villain", RealmType.Stock)!.Value;
        Assert.Null(villain.MeetsEvilOnly(200));      // 120–209 straddles 200: let the game decide
        Assert.True(villain.MeetsEvilOnly(100));
        Assert.False(EvilPointRange.ForTitle("Criminal", RealmType.Stock)!.Value.MeetsEvilOnly(200));
        Assert.False(EvilPointRange.Exact(199.5).MeetsEvilOnly(200));
        Assert.True(EvilPointRange.Exact(200).MeetsEvilOnly(200));
    }

    [Fact]
    public void Below_NarrowsTheTop_OrIsNullWhenItContradicts()
    {
        EvilPointRange villain = EvilPointRange.ForTitle("Villain", RealmType.Stock)!.Value;
        EvilPointRange capped = villain.Below(200)!.Value;
        Assert.False(capped.MeetsEvilOnly(200));
        Assert.Null(capped.MeetsEvilOnly(150));
        Assert.Null(villain.Below(120));              // a Villain can't be below 120
        Assert.Equal("120 to under 200", capped.ToString());
    }

    [Fact]
    public void CanEquip_EvilOnlyValue_GatesOnTheRange()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 98, "AbilVal-0": 200 }""");
        JsonElement robes = doc.RootElement;
        ClassEquipProfile cls = new(1, 8, 9, false);
        EvilPointRange villain = EvilPointRange.ForTitle("Villain", RealmType.Stock)!.Value;

        Assert.True(ItemEquipFilter.CanEquip(robes, 30, cls, AlignmentBucket.Evil, RealmType.Stock, villain));
        Assert.False(ItemEquipFilter.CanEquip(robes, 30, cls, AlignmentBucket.Evil, RealmType.Stock, villain.Below(200)));
        Assert.True(ItemEquipFilter.CanEquip(robes, 30, cls, AlignmentBucket.Evil, RealmType.Stock,
            EvilPointRange.ForTitle("Fiend", RealmType.Stock)));
        Assert.Equal(200, ItemEquipFilter.EvilOnlyValue(robes));
    }

    [Fact]
    public void CanEquip_PlainEvilOnly_BarsSeedyOnParadigm()
    {
        using JsonDocument doc = JsonDocument.Parse("""{ "Abil-0": 98, "AbilVal-0": 0 }""");
        ClassEquipProfile cls = new(1, 8, 9, false);
        Assert.False(ItemEquipFilter.CanEquip(doc.RootElement, 30, cls, AlignmentBucket.Evil, RealmType.ParaMud,
            EvilPointRange.ForTitle("Seedy", RealmType.ParaMud)));
        Assert.True(ItemEquipFilter.CanEquip(doc.RootElement, 30, cls, AlignmentBucket.Evil, RealmType.ParaMud,
            EvilPointRange.ForTitle("Outlaw", RealmType.ParaMud)));
    }

    [Fact]
    public void Spells_EvilOnly_BarsParadigmSeedy()
    {
        EvilPointRange seedy = EvilPointRange.ForTitle("Seedy", RealmType.ParaMud)!.Value;
        Assert.Equal(4, KnownSpellCatalog.CharAlignFor(AlignmentBucket.Evil, seedy));
        Assert.Equal(3, KnownSpellCatalog.CharAlignFor(AlignmentBucket.Evil,
            EvilPointRange.ForTitle("Outlaw", RealmType.ParaMud)));

        SpellFormulaInput evilOnly = new() { Abilities = [new SpellAbility(98, 0)] };
        Assert.False(BuffClassifier.IsAlignmentEligible(evilOnly, AlignmentBucket.Evil, seedy));
        Assert.True(BuffClassifier.IsAlignmentEligible(evilOnly, AlignmentBucket.Evil));
    }
}
