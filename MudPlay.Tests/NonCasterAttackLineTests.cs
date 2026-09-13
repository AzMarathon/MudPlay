using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// "<Actor> shoots an arrow at bandit!" and "<Actor> hurls a fireball at bandit!" are
// the same shape, so the unrecognized-line capture can only suppress it by knowing the
// actor can't cast. These pin that the actor check actually decides it, and that an
// unknown actor never suppresses.
public sealed class NonCasterAttackLineTests
{
    // Paradigm's no-magery classes are Warrior / Witchunter / Ninja / Thief; every
    // other class has at least one spell whose message the catalogue is missing.
    private static Func<string, bool?> Roster(params (string Name, bool? CanCast)[] players) =>
        name =>
        {
            foreach ((string n, bool? canCast) in players)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return canCast;
            return null;   // unknown player
        };

    [Theory]
    [InlineData("Suijin shoots an arrow at bandit!")]
    [InlineData("Suijin swipes at bandit!")]
    [InlineData("Suijin hurls a dagger at nasty bandit!")]
    [InlineData("Suijin stabs at the dark goblin archer.")]
    public void AttackByANonCaster_Matches(string line) =>
        Assert.True(NonCasterAttackLine.Matches(line, Roster(("Suijin", false))));

    [Theory]
    // A Priest CAN cast, so the identical shape might be an uncatalogued spell —
    // it has to stay in the queue.
    [InlineData("Raijin shoots an arrow at bandit!")]
    [InlineData("Raijin hurls a fireball at bandit!")]
    public void SameShapeByACaster_DoesNotMatch(string line) =>
        Assert.False(NonCasterAttackLine.Matches(line, Roster(("Raijin", true))));

    [Fact]
    public void UnknownActor_DoesNotMatch()
    {
        // Nobody knows this player's class, so nothing is proven. A missing roster
        // entry must never be able to hide a real message.
        Assert.False(NonCasterAttackLine.Matches(
            "Stranger shoots an arrow at bandit!", Roster(("Suijin", false))));
    }

    [Theory]
    // A monster's spell landing ON a non-caster names that player as the subject of
    // its own line. Those are real catalogue gaps, so the actor check must be scoped
    // to the attacks-a-target shape and never become a blanket filter on the name.
    [InlineData("Suijin convulses violently!")]
    [InlineData("Suijin screams in agony!")]
    [InlineData("Suijin is blinded by the flash!")]
    public void LinesWhereANonCasterIsTheSubjectNotTheActor_DoNotMatch(string line) =>
        Assert.False(NonCasterAttackLine.Matches(line, Roster(("Suijin", false))));

    [Fact]
    public void EmoteAtATargetByANonCaster_AlsoMatches()
    {
        // The shape covers any "<Actor> <verb> at <target>" line, so a non-caster's
        // emote is suppressed too. That's sound under the same proof — a player who
        // can't cast isn't producing a spell message either way — and it's the " at
        // <target>" requirement, not the verb, that keeps spell lines aimed AT a
        // non-caster out of this.
        Assert.True(NonCasterAttackLine.Matches(
            "Suijin grins at the bandit.", Roster(("Suijin", false))));
    }

    [Fact]
    public void MonsterActor_DoesNotMatch()
    {
        // "The wild dog snaps at Suijin!" leads with "The", not a player name — that
        // form is already a router pattern and needs no roster lookup.
        Assert.False(NonCasterAttackLine.Matches(
            "The wild dog snaps at Suijin!", Roster(("Suijin", false))));
    }

    [Fact]
    public void BlankInput_DoesNotMatch()
    {
        Assert.False(NonCasterAttackLine.Matches(null, Roster()));
        Assert.False(NonCasterAttackLine.Matches("   ", Roster()));
    }
}
