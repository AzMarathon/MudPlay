namespace MudPlay.Models.Profile;

// The character's party-buff plan — a dynamic list of buff slots configured live
// in the Party window (not the Settings tab). Stored as the top-level
// CharacterProfile.PartyBuffs (char-only, like Equipment), decoupled from the
// PartySettings blob so the Party window and the Settings → Party tab never
// clobber each other's writes.
//
// Whether a slot is whole-party or single-target is NOT stored — it's derived
// live from the spell's game-data Targets scope (10 / 13 = whole party; a
// beneficial single-target buff is Targets 2). So the same slot re-classifies
// correctly if the active game-data set changes.
public sealed class PartyBuffSettings
{
    // The buff slots in priority order (the party-bless path walks them top to
    // bottom). Dynamic — the user adds / removes slots in the Party window.
    public System.Collections.Generic.List<PartyBuffSlot> Slots { get; set; } = new();
}

// One party-buff slot. Mutable DTO so the Party-window UI can two-way bind.
//
// Targeting depends on the spell's scope (resolved live, not stored):
//   - Whole-party buff (Targets 10 / 13): fires when WholePartyOn is set — one
//     cast, no target, blankets the party. AllMembers / Targets are ignored.
//   - Single-target buff (Targets 2): cast on OTHER party members one at a time.
//     AllMembers = bless everyone currently in the party AND the room; otherwise
//     bless only the members whose given name is in Targets. Either way the cast
//     only fires for a name that is BOTH a current party member and in the room,
//     so a dissolve / uninvite / rejoin / new party never casts at the wrong
//     person.
public sealed class PartyBuffSlot
{
    // 4-letter spell short-code (e.g. chan), or null/empty for an unconfigured slot.
    public string? Spell { get; set; }

    // Recast lead in seconds: how far before this buff's tracked expiry the
    // CastingDirector recasts it. 0 = wait for actual expiry. Defaults to the
    // shared bless recast margin.
    public int RecastMarginSec { get; set; } = SpellsSettings.DefaultBlessRecastMarginSec;

    // Whole-party slots (Targets 10 / 13): the all-on / all-off toggle. Ignored
    // for single-target slots.
    public bool WholePartyOn { get; set; } = true;

    // Single-target slots (Targets 2): bless every in-party, in-room member,
    // auto-adapting to whatever party you're in. When false, only Targets are
    // blessed. Ignored for whole-party slots.
    public bool AllMembers { get; set; }

    // Single-target slots, when !AllMembers: the specific members to bless, stored
    // as lower-cased given names (the stable, unique player identity). A name that
    // isn't currently in the party + room is silently skipped, so the list can
    // safely outlive the party it was built for.
    public System.Collections.Generic.List<string> Targets { get; set; } = new();
}
