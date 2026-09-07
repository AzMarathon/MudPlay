using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Party-buff picker filter, confirmed against game data: zero energy cost + a
// real duration = a buff (not an attack, not an instant heal/cure/utility spell);
// Targets 2 = single-target-on-a-member, 10 / 13 = whole party. Self-only / enemy
// / item scopes are excluded from IsPartyBuff (IsAnyBuff below includes self-only
// too, for the unified self+party picker).
public sealed class BuffClassifierTests
{
    private static KnownSpell Spell(int targets, int energy, int dur = 10) =>
        new(Number: 1, Short: "abcd", Name: "Test", Magery: 0, MageryLvl: 0,
            ReqLevel: 1, Targets: targets, Formula: new SpellFormulaInput { EnergyCost = energy, Dur = dur });

    [Theory]
    [InlineData(10, true)]
    [InlineData(13, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void IsWholeParty_Only10And13(int targets, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsWholeParty(targets));

    [Theory]
    [InlineData(2, 0, 10, true)]     // single-target beneficial buff (frenzy, divine favour…)
    [InlineData(13, 0, 40, true)]    // full party area (chant, mass frenzy…)
    [InlineData(10, 0, 12, true)]    // divided party area
    [InlineData(2, 500, 10, false)]  // energy cost ⇒ an attack, not a buff
    [InlineData(0, 0, 10, false)]    // self only
    [InlineData(1, 0, 10, false)]    // self only
    [InlineData(4, 0, 10, false)]    // monster (enemy)
    [InlineData(7, 0, 10, false)]    // item
    [InlineData(6, 0, 10, false)]    // generic "any" — not a party-buff scope
    // Zero-energy, Targets-2/13 shape but no duration — an INSTANT effect, same
    // shape as "cure poison" (Targets 2) or "greater healing rain" (Targets 13):
    // there's nothing to maintain, so it must not read as a buff (report: user
    // caught "Add all blesses" offering heal/cure spells this shape used to pass).
    [InlineData(2, 0, 0, false)]
    [InlineData(13, 0, 0, false)]
    public void IsPartyBuff(int targets, int energy, int dur, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsPartyBuff(Spell(targets, energy, dur)));

    [Theory]
    [InlineData(0, 0, 10, true)]     // self-only, maintained (bless, troll skin)
    [InlineData(1, 0, 10, true)]     // self-only, maintained
    [InlineData(2, 0, 10, true)]     // single-target, maintained
    [InlineData(13, 0, 40, true)]    // whole-party, maintained
    [InlineData(0, 0, 0, false)]     // self-only, instant — minor healing / cure poison's shape
    [InlineData(1, 0, 0, false)]     // self-only, instant
    [InlineData(2, 0, 0, false)]     // single-target, instant — cure poison's actual shape
    [InlineData(13, 0, 0, false)]    // whole-party, instant — greater healing rain's actual shape
    [InlineData(0, 500, 10, false)]  // energy cost ⇒ an attack, not a buff, regardless of duration
    [InlineData(4, 0, 10, false)]    // monster (enemy) scope, even if it happened to carry a duration
    public void IsAnyBuff(int targets, int energy, int dur, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAnyBuff(Spell(targets, energy, dur)));

    [Fact]
    public void HasDuration_LevelScaledDurationWithZeroBase_CountsAsMaintained()
    {
        // Dur itself is 0 but DurInc/DurIncLVLs mean it grows from level 1 — still
        // a real, maintainable duration (mirrors RegenSpellClassifier's HoT gate).
        SpellFormulaInput formula = new() { Dur = 0, DurInc = 3, DurIncLVLs = 2 };
        Assert.True(BuffClassifier.HasDuration(formula));
    }

    [Fact]
    public void HasDuration_AllZero_IsInstant()
    {
        Assert.False(BuffClassifier.HasDuration(new SpellFormulaInput()));
    }
}
