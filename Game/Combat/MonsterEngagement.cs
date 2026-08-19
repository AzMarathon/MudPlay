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

    // A monster instance the user MANUALLY engaged is engageable regardless of its
    // species relationship — once you hand-attack a passive neutral it behaves like a
    // hostile (keeps attacking you until dead), so the engine takes over killing it. The
    // per-instance flag is keyed on RawName by the caller (species-keyed overlay + an
    // instance-keyed override are different key spaces, so the flag can't live in the
    // pure static — the OR-rule does).
    public static bool IsEngageable(MonsterOverlay? overlay, bool userEngagedInstance)
        => userEngagedInstance || IsEngageable(overlay);
}
