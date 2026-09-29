using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// DamageLineAttributor against the engine's damage wordings (Stock 1.11p message table
// and hardcoded melee forms): who dealt it, who took it, or unknown (null).
public sealed class DamageLineAttributorTests
{
    private static readonly string[] Names = { "large orc", "orc", "Bob" };

    [Theory]
    // Our own melee and spells.
    [InlineData("You slash the large orc for 12 damage!", "You", "large orc")]
    [InlineData("You cast fireball at orc for 12 damage!", "You", "orc")]
    [InlineData("Your streaking arrow strikes orc for 12 damage!", "You", "orc")]
    [InlineData("Your foes are drenched in acid as the wind batters them for 12 damage!", "You", null)]
    // Someone else's melee and spells, named by the room roster.
    [InlineData("Bob slashes large orc for 12 damage!", "Bob", "large orc")]
    [InlineData("Bob casts fireball on orc for 12 damage!", "Bob", "orc")]
    [InlineData("The orc bites Bob for 12 damage!", "orc", "Bob")]
    // Monster attacks on us, whatever the verb phrase.
    [InlineData("The large orc bites you for 12 damage!", "large orc", "You")]
    [InlineData("The orc claws you with its pincers for 12 damage!", "orc", "You")]
    [InlineData("The orc bites your ankle for 12 damage!", "orc", "You")]
    [InlineData("The orc casts acid bolt on you for 12 damage!", "orc", "You")]
    // The victim's own view names only the attacker.
    [InlineData("The orc throws a flask, which explodes for 12 damage!", "orc", "You")]
    [InlineData("The orc releases a bolt of force from its palm for 12 damage!", "orc", "You")]
    // Damage to us that names no source.
    [InlineData("Acid sears you for 12 damage!", null, "You")]
    [InlineData("You are poisoned for 12 damage!", null, "You")]
    [InlineData("You combust for 12 damage!", null, "You")]
    [InlineData("Your blood is drained for 12 damage!", null, "You")]
    // A named victim with no named source.
    [InlineData("Bob is scorched for 12 damage!", null, "Bob")]
    [InlineData("The large orc's life is drained for 12 damage!", null, "large orc")]
    [InlineData("Dark flame sears orc for 12 damage!", null, "orc")]
    // An area effect names neither.
    [InlineData("An earthquake rocks the room for 12 damage!", null, null)]
    public void NamesBothSides(string line, string? source, string? target)
    {
        Assert.True(DamageLineAttributor.TryAttribute(line, Names, out DamageAttribution a));
        Assert.Equal(source, a.Source);
        Assert.Equal(target, a.Target);
        Assert.Equal(12, a.Amount);
    }

    // The Stock message table's other damage wordings (spells, traps, hazards).
    [Theory]
    [InlineData("You take 12 damage from the flames!", null, "You", true)]
    [InlineData("You take 12 lightning damage!", null, "You", true)]
    [InlineData("You took 12 damage!", null, "You", true)]
    [InlineData("The box opens, and then snaps shut tightly. You take 12 damage!", null, "You", true)]
    [InlineData("The orc takes 12 acid damage!", null, "orc", false)]
    [InlineData("Bob takes 12 damage from the fall!", null, "Bob", true)]
    [InlineData("You fall to the ground with a thud, taking 12 damage!", null, "You", true)]
    [InlineData("Bob falls to the ground with a thud, taking 12 damage!", null, "Bob", true)]
    [InlineData("The flask explodes, causing 12 damage!", null, null, false)]
    [InlineData("Your flesh dissolves, causing you 12 damage!", null, "You", false)]
    [InlineData("A counterstrike at you does 12 damage!", null, "You", false)]
    [InlineData("The orc summons a firestorm upon the room, doing 12 damage!", "orc", "You", false)]
    [InlineData("Bob sings the song of blasting to you, causing 12 damage!", "Bob", "You", false)]
    [InlineData("You sing the song of blasting, causing 12 damage to your foes!", "You", null, false)]
    [InlineData("You summon a demon upon the room doing 12 damage!", "You", null, false)]
    // A room spell's caster view: only the caster sees it aimed at "your foes".
    [InlineData("A hellish storm of fire and brimstone scorches your foes for 12 damage!", "You", null, false)]
    [InlineData("You summon a demon which drains your enemies for 12 damage!", "You", null, false)]
    // Stock's evil punishment / a sysop's punish: nobody in the room dealt it.
    [InlineData("A bolt of lightning from the heavens strikes you for 12 points damage!", null, "You", true)]
    public void OtherDamageWordings(string line, string? source, string? target, bool noDealer)
    {
        Assert.True(DamageLineAttributor.TryAttribute(line, Names, out DamageAttribution a));
        Assert.Equal((source, target, 12, noDealer), (a.Source, a.Target, a.Amount, a.NoDealer));
    }

    // Heals and armour absorbs name "damage" but aren't any.
    [Theory]
    [InlineData("You cast minor healing on Bob, healing 12 damage!")]
    [InlineData("Bob is healed of 12 damage!")]
    [InlineData("You cast regeneration on Bob, regenerating 12 damage!")]
    [InlineData("The healer heals you of 12 damage!")]
    [InlineData("The orc smacks you, but your armour absorbs the damage!")]
    [InlineData("You resist the poison, and take only partial damage!")]
    public void HealsAndAbsorbs_AreNotDamage(string line)
        => Assert.False(DamageLineAttributor.TryAttribute(line, Names, out _));

    // A condition or effect on us has no dealer at all, unlike a spell whose caster
    // the line just doesn't name.
    [Theory]
    [InlineData("You are poisoned for 12 damage!", true)]
    [InlineData("You combust for 12 damage!", true)]
    [InlineData("Your blood is drained for 12 damage!", true)]
    [InlineData("Acid sears you for 12 damage!", false)]
    public void NoDealer_OnlyForConditionsOnYou(string line, bool noDealer)
    {
        Assert.True(DamageLineAttributor.TryAttribute(line, Names, out DamageAttribution a));
        Assert.Equal(noDealer, a.NoDealer);
    }

    [Theory]
    [InlineData("You take 5 damage for bashing the door!")]
    [InlineData("The orc swings at you, but misses!")]
    [InlineData("")]
    public void NotADamageLine(string line)
        => Assert.False(DamageLineAttributor.TryAttribute(line, Names, out _));
}
