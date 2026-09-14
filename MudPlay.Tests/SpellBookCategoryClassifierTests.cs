using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Confirmed against real Spells.json shapes (Paradigm set) pulled while building
// the feature: god's wrath (EnergyCost 1000, Abil 17 Damage(-MR)) is Attacks
// only; major/greater healing (EnergyCost 0, Abil 18 Heal) is Heals only; bless
// (EnergyCost 0, Dur 40, Targets 2) is Buffs only; chant / mass frenzy
// (EnergyCost 0, Dur > 0, Targets 13) are both Buffs AND PartyOrAoe; cure poison
// (EnergyCost 0, Dur 0, Abil 20 CurePoison, no Abil 18) is Heals via the
// CurePoison code, not via MaxHeal.
public sealed class SpellBookCategoryClassifierTests
{
    private static SpellFormulaInput? NoChain(int _) => null;

    private static KnownSpell Spell(
        int targets, int energy, int dur = 0, int minBase = 0, int maxBase = 0,
        params SpellAbility[] abilities) =>
        new(Number: 1, Short: "abcd", Name: "Test", Magery: 0, MageryLvl: 0,
            ReqLevel: 1, Targets: targets,
            Formula: new SpellFormulaInput
            {
                EnergyCost = energy, Dur = dur, MinBase = minBase, MaxBase = maxBase,
                Abilities = abilities,
            });

    [Fact]
    public void All_AlwaysMatches_RegardlessOfShape()
    {
        KnownSpell attack = Spell(targets: 8, energy: 1000, abilities: new SpellAbility(17, 0));
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.All, attack, 1, NoChain));
    }

    [Fact]
    public void Heals_MatchesHealAbility_GodsWrathShapeDoesNot()
    {
        // major healing's real shape: EnergyCost 0, Abil 18 (Heal).
        KnownSpell heal = Spell(targets: 2, energy: 0, minBase: 8, maxBase: 20,
            abilities: new SpellAbility(18, 0));
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.Heals, heal, 1, NoChain));

        // god's wrath's real shape: EnergyCost 1000, Abil 17 (Damage(-MR)) — no
        // Heal ability, so it must not read as a heal even though it carries a
        // magnitude range.
        KnownSpell attack = Spell(targets: 8, energy: 1000, minBase: 15, maxBase: 20,
            abilities: new SpellAbility(17, 0));
        Assert.False(SpellBookCategoryClassifier.Matches(SpellBookCategory.Heals, attack, 1, NoChain));
    }

    [Fact]
    public void Heals_MatchesCurePoisonAbility_EvenWithNoHealMagnitude()
    {
        // cure poison's real shape: EnergyCost 0, Dur 0, Abil 20 (CurePoison) —
        // no Abil 18, so SpellCalculator.MaxHeal alone would miss it; the
        // dedicated CurePoison check is what catches it.
        KnownSpell cure = Spell(targets: 2, energy: 0, dur: 0, abilities: new SpellAbility(20, 0));
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.Heals, cure, 1, NoChain));
    }

    [Fact]
    public void Buffs_DelegatesToBuffClassifier()
    {
        // bless's real shape: EnergyCost 0, Dur 40, Targets 2 (single-target).
        KnownSpell bless = Spell(targets: 2, energy: 0, dur: 40);
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.Buffs, bless, 1, NoChain));

        // An instant (no duration) spell of the same scope/energy is not a buff
        // — same shape BuffClassifierTests pins for cure poison / minor healing.
        KnownSpell instant = Spell(targets: 2, energy: 0, dur: 0);
        Assert.False(SpellBookCategoryClassifier.Matches(SpellBookCategory.Buffs, instant, 1, NoChain));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    public void Attacks_EnergyCostAboveZero(int energy, bool expected)
    {
        KnownSpell spell = Spell(targets: 8, energy: energy);
        Assert.Equal(expected, SpellBookCategoryClassifier.Matches(SpellBookCategory.Attacks, spell, 1, NoChain));
    }

    [Theory]
    [InlineData(0, false)]   // User
    [InlineData(1, false)]   // Self
    [InlineData(2, false)]   // Self or User
    [InlineData(3, true)]    // Divided Area (not self)
    [InlineData(4, false)]   // Monster
    [InlineData(5, true)]    // Divided Area (incl self)
    [InlineData(6, false)]   // Any
    [InlineData(7, false)]   // Item
    [InlineData(8, false)]   // Monster or User
    [InlineData(9, true)]    // Divided Attack Area
    [InlineData(10, true)]   // Divided Party Area
    [InlineData(11, true)]   // Full Area
    [InlineData(12, true)]   // Full Attack Area
    [InlineData(13, true)]   // Full Party Area
    public void PartyOrAoe_MatchesAreaAndWholePartyScopes(int targets, bool expected)
    {
        KnownSpell spell = Spell(targets, energy: 0);
        Assert.Equal(expected, SpellBookCategoryClassifier.Matches(SpellBookCategory.PartyOrAoe, spell, 1, NoChain));
    }

    [Fact]
    public void WholePartyBuff_MatchesBothBuffsAndPartyOrAoe()
    {
        // chant / mass frenzy's real shape: EnergyCost 0, Dur > 0, Targets 13.
        // A spell can legitimately satisfy more than one tab.
        KnownSpell chant = Spell(targets: 13, energy: 0, dur: 40);
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.Buffs, chant, 1, NoChain));
        Assert.True(SpellBookCategoryClassifier.Matches(SpellBookCategory.PartyOrAoe, chant, 1, NoChain));
        Assert.False(SpellBookCategoryClassifier.Matches(SpellBookCategory.Attacks, chant, 1, NoChain));
        Assert.False(SpellBookCategoryClassifier.Matches(SpellBookCategory.Heals, chant, 1, NoChain));
    }
}
