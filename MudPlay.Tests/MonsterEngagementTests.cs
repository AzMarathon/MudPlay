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

    // The per-instance override: a passive neutral the user hand-engaged fights like a
    // hostile until dead, so it's engageable regardless of its species relationship.
    [Fact]
    public void UserEngagedInstance_MakesPassiveNeutralEngageable()
    {
        MonsterOverlay passive = new() { Relationship = MonsterRelationship.Neutral };
        Assert.False(MonsterEngagement.IsEngageable(passive, userEngagedInstance: false));
        Assert.True(MonsterEngagement.IsEngageable(passive, userEngagedInstance: true));
    }

    // The override never drags in a Friend/Flee/Hangup — it only widens what the
    // OR-rule already allows plus the explicit user-engaged flag, so a non-engaged
    // instance still follows the pure relationship rule.
    [Fact]
    public void UserEngagedFalse_LeavesFriendSafe()
        => Assert.False(MonsterEngagement.IsEngageable(
            new MonsterOverlay { Relationship = MonsterRelationship.Friend },
            userEngagedInstance: false));
}
