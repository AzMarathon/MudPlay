namespace MudPlay.Game.Combat;

// How a single combat line was interpreted, derived GENERICALLY from line colour +
// wording + target — no per-monster message data. "You" = the local player;
// "Other" = another player / party member. Only lines dispatched inside a
// *Combat Engaged* … *Combat Off* window are classified; everything else is None.
public enum CombatLineKind
{
    None = 0,          // not a recognized combat outcome (or outside a combat window)
    PlayerHit,         // your attack lands ("You … for N damage!")
    PlayerMiss,        // your attack whiffs ("You miss your throw at X!")
    MonsterHitYou,     // a monster's attack lands on you ("… you for N damage!")
    MonsterHitOther,   // a monster's attack lands on someone else
    ArmorBlockYou,     // a monster's swing is deflected by YOUR armor (glances off)
    ArmorBlockOther,   // deflected by someone else's armor
    DodgeYou,          // YOU dodge a monster's swing
    DodgeOther,        // someone else dodges a monster's swing
    MonsterMissYou,    // a monster swings at you and misses (no contact)
    MonsterMissOther,  // a monster swings at someone else and misses
    Reflect,           // a worn thorns/ShockShield item strikes the attacker back
                       // ("The armour spikes stab <monster> for N damage!") — the
                       // monster is the victim, so it's our (or a party member's)
                       // retaliation, NOT a monster hit
}
