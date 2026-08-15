using MudPlay.Models.GameData;

namespace MudPlay.Game.Combat;

// Single source of truth for "should auto-combat engage this monster?" A monster is
// engageable when its Relationship is Enemy (the default for un-tagged monsters), OR
// it's a Neutral the user flagged KillOnSight. Neutrals never attack first, so an
// un-flagged neutral is left alone — and a room of passive neutrals stays safe to
// rest in. Friend / Flee / Hangup are never engaged.
public static class MonsterEngagement
{
    public static bool IsEngageable(MonsterOverlay? overlay)
    {
        MonsterRelationship rel = overlay?.Relationship ?? MonsterRelationship.Enemy;
        return rel switch
        {
            MonsterRelationship.Enemy   => true,
            MonsterRelationship.Neutral => overlay?.KillOnSight == true,
            _                           => false,
        };
    }
}
