namespace MudPlay.Models.Profile;

// The character's UNIFIED buff plan — one dynamic list holding every automated
// buff, self and party alike, configured live in the Buff Watchdog window. Stored
// as the top-level CharacterProfile.PartyBuffs (char-only, like Equipment). (Name
// kept for now for storage compatibility; the type is renamed Buff* in a later
// cleanup — it is no longer party-only.)
//
// A slot's spell scope is derived live from the game-data Targets code (0 / 1 =
// self-only; 2 = single-target, castable on self and/or members; 10 / 13 = whole
// party), so the same slot re-classifies correctly across game-data sets. The
// targeting flags + conditions below say WHO and WHEN; the scope says which of
// them apply.
public sealed class PartyBuffSettings
{
    // The buff slots in priority order (the buff path walks them top to bottom).
    // Dynamic — the user adds / removes slots in the Buff Watchdog.
    public System.Collections.Generic.List<PartyBuffSlot> Slots { get; set; } = new();
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
public sealed class PartyBuffSlot
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

    // Whole-party slots (Targets 10 / 13): the all-on / all-off toggle.
    public bool WholePartyOn { get; set; } = true;

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
}
