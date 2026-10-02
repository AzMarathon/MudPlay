using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

public sealed class BuffOffenseCalculatorTests
{
    // Paradigm shadowform's shape: Stealth / BS min / BS max take the spell's
    // magnitude (5, +1 per 3 levels), BS accuracy a flat 5; 115 / 178 are a
    // message and a look description, not offense.
    private static KnownSpell Shadowform(int targets = 1) =>
        new(Number: 130, Short: "shad", Name: "shadowform", Magery: 1, MageryLvl: 1, ReqLevel: 7,
            Targets: targets,
            Formula: new SpellFormulaInput
            {
                ReqLevel = 7, Cap = 30, MinBase = 5, MaxBase = 5, MaxInc = 1, MaxIncLVLs = 3,
                Abilities = new[]
                {
                    new SpellAbility(27, 0), new SpellAbility(115, 70), new SpellAbility(116, 5),
                    new SpellAbility(178, 4157), new SpellAbility(117, 0), new SpellAbility(118, 0),
                },
            });

    // Level 8: 5 + 8/3 = 7 — the live `stat` Stealth went 83 → 90 on the cast.
    [Fact]
    public void Shadowform_AtLevel8_FoldsSpellMagnitude()
    {
        BuffOffense b = BuffOffenseCalculator.Fold(new[] { Shadowform() }, level: 8);
        Assert.Equal(7, b.Stealth);
        Assert.Equal(5, b.BsAccuracy);
        Assert.Equal(7, b.BsMin);
        Assert.Equal(7, b.BsMax);
    }

    // Report paradigm-20261002-142319: the game rolls a buff's value as it's cast
    // (shadowform at level 15 gives 5 to 10), and Monster Intel judged a sure kill on
    // the top of the roll. The lowest fold is what a cast is certain to give; a flat
    // value (BS accuracy 5) is the same in both.
    [Fact]
    public void Lowest_FoldsTheBottomOfTheRoll_AndLeavesFlatValuesAlone()
    {
        BuffOffense low = BuffOffenseCalculator.Fold(new[] { Shadowform() }, level: 15, lowest: true);
        BuffOffense high = BuffOffenseCalculator.Fold(new[] { Shadowform() }, level: 15);

        Assert.Equal(5, low.BsMin);
        Assert.Equal(5, low.BsMax);
        Assert.Equal(5, low.Stealth);
        Assert.Equal(10, high.BsMin);
        Assert.Equal(10, high.BsMax);
        Assert.Equal(10, high.Stealth);
        Assert.Equal(5, low.BsAccuracy);
        Assert.Equal(5, high.BsAccuracy);
    }

    // Stealth shows in `stat` while the buff is up, so an active buff's Stealth
    // isn't added again — its backstab bonuses still are.
    [Fact]
    public void BuffAlreadyUp_SkipsStealthOnly()
    {
        BuffOffense b = BuffOffenseCalculator.Fold(new[] { Shadowform() }, level: 8, isUp: _ => true);
        Assert.Equal(0, b.Stealth);
        Assert.Equal(5, b.BsAccuracy);
        Assert.Equal(7, b.BsMin);
    }

    [Fact]
    public void AffectsOffense_NeedsSelfScopeAndAnOffenseAbility()
    {
        Assert.True(BuffOffenseCalculator.AffectsOffense(Shadowform()));
        Assert.False(BuffOffenseCalculator.AffectsOffense(Shadowform(targets: 8)));   // enemy target
        KnownSpell acOnly = Shadowform() with
        {
            Formula = new SpellFormulaInput { ReqLevel = 1, Cap = 50, Abilities = new[] { new SpellAbility(2, 10) } },
        };
        Assert.False(BuffOffenseCalculator.AffectsOffense(acOnly));
    }
}
