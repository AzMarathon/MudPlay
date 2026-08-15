using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// Auto-combat engages Enemies (the default for un-tagged monsters) and Neutrals the
// user flagged KillOnSight; everything else (passive neutrals, Friend/Flee/Hangup)
// is left alone.
public sealed class MonsterEngagementTests
{
    [Fact]
    public void NullOverlay_DefaultsToEnemy_Engageable()
        => Assert.True(MonsterEngagement.IsEngageable(null));

    [Fact]
    public void Enemy_Engageable()
        => Assert.True(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Enemy }));

    [Fact]
    public void PassiveNeutral_NotEngageable()
        => Assert.False(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Neutral }));

    [Fact]
    public void KillOnSightNeutral_Engageable()
        => Assert.True(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Neutral, KillOnSight = true }));

    [Fact]
    public void KillOnSightWithoutNeutral_HasNoEffect_FriendStaysSafe()
        => Assert.False(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Friend, KillOnSight = true }));

    [Fact]
    public void Flee_And_Hangup_NotEngageable()
    {
        Assert.False(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Flee }));
        Assert.False(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Hangup }));
    }
}
