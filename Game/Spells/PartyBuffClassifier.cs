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

    // A beneficial single-target buff cast on another player.
    public static bool IsSingleTargetBuff(int targets) => targets is 2;

    // True when the spell belongs in the party-buff picker: a zero-energy buff
    // whose scope targets another player or the whole party.
    public static bool IsPartyBuff(in KnownSpell spell) =>
        spell.Formula.EnergyCost == 0
        && (IsSingleTargetBuff(spell.Targets) || IsWholeParty(spell.Targets));
}
