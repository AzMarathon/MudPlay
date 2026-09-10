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

    // Whole-party slots (Targets 10 / 13): the all-on / all-off toggle for casting
    // it party-wide while in a party.
    public bool WholePartyOn { get; set; } = true;

    // Whole-party slots: also cast it while solo. A whole-party cast still lands on
    // a lone character (a party of one — see GAME_MECHANICS.md), so when set the
    // slot fires solo under the self-bless timing gates. Defaults on to preserve the
    // solo-casts behaviour; untick to make a whole-party buff party-only.
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

    // Roll spells: reroll while the rolled mana-regen contribution lands BELOW this
    // value (the min gate). null = rerolling off even if RerollCount > 0. On Paradigm
    // this is read from `abil 145`; on Stock it's a 0-100% of the best-possible tick.
    public int? RerollThreshold { get; set; }

    // Roll spells: reroll without a cap — keep re-casting until the roll clears the
    // threshold (or the mana floor suspends the cycle, resuming as mana recovers).
    // Spares the user from setting an obscene RerollCount to approximate "unlimited";
    // when true, RerollCount is ignored.
    public bool RerollInfinite { get; set; }
}
