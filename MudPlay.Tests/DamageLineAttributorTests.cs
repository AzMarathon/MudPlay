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

    [Theory]
    [InlineData("You take 5 damage for bashing the door!")]
    [InlineData("The orc swings at you, but misses!")]
    [InlineData("")]
    public void NotADamageLine(string line)
        => Assert.False(DamageLineAttributor.TryAttribute(line, Names, out _));
}
