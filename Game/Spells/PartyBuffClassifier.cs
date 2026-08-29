namespace MudPlay.Game.Spells;

// Classifies a spell as a party buff for the Party-window picker and the casting
// path. The primary signal is a ZERO energy cost (a buff, not an attack — attack
// spells cost 500-1000 energy); the Targets scope then splits whole-party from
// single-target. Confirmed against game data across stock + Paradigm sets:
//   Targets 2       = single-target beneficial buff, cast on one other member
//                     (frenzy, divine favour, blood ritual, regeneration…)
//   Targets 10 / 13 = whole-party buff, one cast blankets the party
//                     (chant, mass frenzy, unholy fanaticism, rejuvenating field…)
// Self-only (0 / 1), enemy (4 / 8 / 9 / 12), item (7), and generic-area scopes
// are excluded — they aren't beneficial party buffs.
public static class PartyBuffClassifier
{
    // Whole-party scope codes (one cast, no target). Matches AppServices.IsPartyWideBuff.
    public static bool IsWholeParty(int targets) => targets is 10 or 13;

    // A beneficial single-target buff cast on another player (or, for the unified
    // list, castable on ourselves too).
    public static bool IsSingleTargetBuff(int targets) => targets is 2;

    // A self-only beneficial buff (bless, troll skin, and kin) — only castable on us.
    public static bool IsSelfBuff(int targets) => targets is 0 or 1;

    // True when the spell belongs in the party-buff picker: a zero-energy buff
    // whose scope targets another player or the whole party.
    public static bool IsPartyBuff(in KnownSpell spell) =>
        spell.Formula.EnergyCost == 0
        && (IsSingleTargetBuff(spell.Targets) || IsWholeParty(spell.Targets));

    // True when the spell belongs in the UNIFIED buff picker: a zero-energy buff we
    // can maintain on ourselves, a member, or the whole party (self / single-target /
    // whole-party scopes). Attacks (energy > 0), enemy / area / item-target scopes
    // are excluded.
    public static bool IsAnyBuff(in KnownSpell spell) =>
        spell.Formula.EnergyCost == 0
        && (IsSelfBuff(spell.Targets) || IsSingleTargetBuff(spell.Targets) || IsWholeParty(spell.Targets));
}
