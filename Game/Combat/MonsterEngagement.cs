using MudPlay.Models.GameData;

namespace MudPlay.Game.Combat;

// Single source of truth for "should auto-combat engage this monster?" A monster is
// engageable when its Relationship is Enemy (the default for a monster with none set
// and no alignment to take one from), OR
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

    // The relationship a monster the shipped seed doesn't know takes from its
    // Monsters-table Align (Client policy, user 2026-10-25; GAME_MECHANICS "Monster
    // `Align` values and your alignment-title ladder"): any good is a Friend, Neutral
    // a Neutral, any evil (Neutral Evil included) an Enemy. Null for a value outside
    // 0–6, which then reads as Enemy like a monster with nothing set.
    public static MonsterRelationship? DefaultForAlignment(int align) => align switch
    {
        0 or 4           => MonsterRelationship.Friend,    // Good, Lawful Good
        3                => MonsterRelationship.Neutral,
        1 or 2 or 5 or 6 => MonsterRelationship.Enemy,     // Evil, Chaotic Evil, Neutral Evil, Lawful Evil
        _                => null,
    };
}
