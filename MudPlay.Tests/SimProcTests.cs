using MudPlay.Game;
using MudPlay.Game.Simulation;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// A monster's hit spell is cast with no level: base damage and duration, whatever the
// spell's per-level slope. On Stock only a Damage (1) spell with a duration burns;
// Damage-MR (17) hits on landing. Paradigm burns a timed damage spell (envelops).
public sealed class SimProcTests
{
    private static SpellFormulaInput Spell(int code) => new()
    {
        Number = 884, MinBase = 6, MaxBase = 8, MinInc = 4, MinIncLVLs = 1, MaxInc = 10, MaxIncLVLs = 1,
        Dur = 10, DurInc = 5, DurIncLVLs = 1, ReqLevel = 20,
        Abilities = new[] { new SpellAbility(code, 0) },
    };

    [Fact]
    public void ReadsBaseValuesWithNoLevel()
    {
        SimProc proc = SimProc.From(Spell(SpellCalculator.AbilDamage), RealmType.Stock)!;

        Assert.Equal(6, proc.DamageMin);
        Assert.Equal(8, proc.DamageMax);
        Assert.Equal(10 * SpellCalculator.SpellRoundSecondsWallClock, proc.DurationSeconds);
    }

    [Theory]
    [InlineData(1, RealmType.Stock, true)]
    [InlineData(17, RealmType.Stock, false)]
    [InlineData(17, RealmType.ParaMud, true)]
    public void WhichTimedDamageSpellsBurn(int code, RealmType realm, bool burns) =>
        Assert.Equal(burns, SimProc.From(Spell(code), realm)!.DamageOverTime);
}
