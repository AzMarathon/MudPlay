namespace MudPlay.Models.Profile;

// The character's UNIFIED buff plan — one dynamic list holding every automated
// buff, self and party alike, configured live in the Buff Watchdog window. Stored
// as the top-level CharacterProfile.PartyBuffs (char-only, like Equipment). The
// PartyBuffs property/key name is kept for storage compatibility even though the
// type is named Buff* — it is no longer party-only.
//
// A slot's spell scope is derived live from the game-data Targets code (0 / 1 =
// self-only; 2 = single-target, castable on self and/or members; 10 / 13 = whole
// party), so the same slot re-classifies correctly across game-data sets. The
// targeting flags + conditions below say WHO and WHEN; the scope says which of
// them apply.
public sealed class BuffSettings
{
    // The buff slots in the user's config-list order. Dynamic — the user adds /
    // removes / re-arranges slots in the Buff Watchdog. The cast-priority walk
    // order is derived from this list per the two flags below (see
    // BuffPriorityOrder.InPriorityOrder).
    public System.Collections.Generic.List<BuffSlot> Slots { get; set; } = new();

    // Layout flag. false (default) = the config table auto-groups slots by type
    // (self/single-target → whole-party → item-on-use) and new buffs sort into
    // their group. true = the user hand-arranged the rows; the list is shown in
    // its stored order and new buffs append at the bottom. Flips true the first
    // time the user drags / moves a row.
    public bool ManualOrder { get; set; }

    // Cast-priority flag, INDEPENDENT of ManualOrder. false (default) = the
    // engine casts due buffs in category order (self → whole-party → item)
    // regardless of how the rows are arranged. true = the engine casts them in
    // the exact stored list order (top to bottom). The two modes are identical
    // until the rows are re-arranged.
    public bool PriorityTopDown { get; set; }
}

// One buff slot (self and/or party). Mutable DTO so the Buff Watchdog UI two-way
// binds.
//
// Targeting (which flags apply is derived from the spell's Targets scope):
//   - Self-only spell (Targets 0 / 1): CastOnSelf is the only target.
//   - Single-target spell (Targets 2): any of CastOnSelf, AllMembers, or the
//     chosen given names in Targets — cast on each selected recipient one per pass.
//     A member is only ever targeted while BOTH in the party and reachable, so
//     churn never casts at the wrong person.
//   - Whole-party spell (Targets 10 / 13): WholePartyOn — one cast blankets the
//     party (and lands on us).
public sealed class BuffSlot
{
    // 4-letter spell short-code (e.g. chan) or a #item-cast token, or null/empty
    // for an unconfigured slot.
    public string? Spell { get; set; }

    // Recast lead in seconds: how far before this buff's tracked expiry the
    // CastingDirector recasts it. 0 = wait for actual expiry. Defaults to the
    // shared bless recast margin.
    public int RecastMarginSec { get; set; } = SpellsSettings.DefaultBlessRecastMarginSec;

    // ----- Targeting -------------------------------------------------

    // Cast on OURSELF. The only target for a self-only spell; one option among
    // members for a single-target spell.
    public bool CastOnSelf { get; set; }

    // Whole-party slots (Targets 10 / 13): the master all-on / all-off toggle.
    // When false the slot never casts, including while solo.
    public bool WholePartyOn { get; set; } = true;

    // Whole-party slots: while WholePartyOn is enabled, also cast it while solo. A
    // whole-party cast still lands on a lone character (a party of one — see
    // GAME_MECHANICS.md), so when set the slot fires solo under the same
    // conditions as in a party. This is subordinate to WholePartyOn: it can never re-enable an off slot.
    public bool CastSolo { get; set; } = true;

    // Single-target slots (Targets 2): bless every in-party member, auto-adapting
    // to whatever party you're in. When false, only Targets are blessed.
    public bool AllMembers { get; set; }

    // Single-target slots, when !AllMembers: the specific members to bless, stored
    // as lower-cased given names (the stable, unique player identity). A name that
    // isn't currently in the party is silently skipped, so the list safely outlives
    // the party it was built for.
    public System.Collections.Generic.List<string> Targets { get; set; } = new();

    // ----- Conditions (per-slot gates) -------------------------------

    // Cast this buff only while mana is at or above this much: a percent of max
    // mana, or a raw mana / kai amount when Settings → Health reads its mana
    // thresholds as absolute values (HealthSettings.MaThresholdMode). It keeps mana
    // back for heals. A free item-cast buff ignores it. Was one value for every
    // buff (HealthSettings.BlessIfAboveMa) until profile schema 5.
    public int BlessIfAboveMa { get; set; } = DefaultBlessIfAboveMa;

    // What a new buff starts at for its mana floor. A buff that existed before the
    // floor became per-buff took the character's old shared value instead.
    public const int DefaultBlessIfAboveMa = 50;

    // Cast this buff while a triggered recovery rest is under way — on ourselves or
    // on the party alike. Off = wait until the rest is done. Was one switch for
    // every self cast (SpellsSettings.SelfBlessWhileResting) and another for every
    // party cast (PartySettings.BlessWhileResting) until profile schemas 5 and 6.
    public bool BlessWhileResting { get; set; }

    // Cast this buff during a fight, solo or in a party. Off = wait until the room
    // is clear. Was SpellsSettings.SelfBlessDuringCombat for self casts and
    // PartySettings.BlessDuringCombat for party casts until schemas 5 and 6.
    public bool BlessDuringCombat { get; set; }

    // A priority buff: cast in the Priority buffs slot of the spell-type priority
    // (Settings → Spells), which by default sits ahead of the minor self heal, instead
    // of with the other buffs. Starred on its timer bar.
    public bool PriorityBuff { get; set; }

    // Cast only once we've rested our HP up to the rest-max target — a "topped-off,
    // ready for the next fight" buff. Recasts while up there; a triggered rest-if-below
    // suspends it until we've rested back to max. Replaces the old dedicated
    // WhenHpFullSpell slot.
    public bool OnlyWhenHpFull { get; set; }

    // Same as OnlyWhenHpFull, on the mana pool. Replaces WhenMaFullSpell.
    public bool OnlyWhenMaFull { get; set; }

    // Light spell only: cast only when the current room is dark (the old RoomLightSpell
    // behaviour). Unchecked ⇒ treat it as an ordinary maintained buff.
    public bool OnlyWhenDark { get; set; }

    // Mana-regen spell only: cast it as a pre-rest top-up (its prior behaviour).
    // Unchecked ⇒ keep it up all the time like a normal buff.
    public bool CastBeforeRestingForMana { get; set; }

    // Roll spells (flux / ntap / prfl and kin): how many times to re-cast chasing a
    // better mana-regen roll before accepting what landed. 0 = don't reroll (the
    // spell just recasts on expiry).
    public int RerollCount { get; set; }

    // Roll spells: reroll while the rolled mana-regen percent lands BELOW this value
    // (the min gate). null = rerolling off even if RerollCount > 0. Paradigm reads the
    // roll from `abil 145`; Stock reads it back off the natural mana tick.
    public int? RerollThreshold { get; set; }

    // True once RerollThreshold is in the rolled-percent unit. Stock thresholds saved
    // before that were a desired mana tick, converted once when the character's tick
    // inputs are known.
    public bool RerollThresholdIsRoll { get; set; }

    // Draw items (a deck of cards): the spell numbers of the outcomes the user does
    // NOT want. Drawing one of these re-uses the item on the next cycle until a wanted
    // one lands. Empty = every outcome is accepted.
    public System.Collections.Generic.List<int> RejectedOutcomes { get; set; } = new();

    // Roll spells: reroll without a cap — keep re-casting until the roll clears the
    // threshold (or the mana floor suspends the cycle, resuming as mana recovers).
    // Spares the user from setting an obscene RerollCount to approximate "unlimited";
    // when true, RerollCount is ignored.
    public bool RerollInfinite { get; set; }
}
