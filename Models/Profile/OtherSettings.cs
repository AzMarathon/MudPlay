namespace MudPlay.Models.Profile;

// Per-character "Other" settings — the misc bucket. Stored as the "Other" entry
// in CharacterProfile.Settings.
public sealed class OtherSettings
{
    // Block @do suicide / @party suicide when remaining lives are at or below
    // this threshold. Default 5 — protects players who haven't yet built up a
    // comfortable lives buffer. Setting to 0 allows forced suicide through all
    // lives. Max lives in MajorMUD is 9, so the UI clamps this to 0..9. Pushed
    // into Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold.
    //
    // The engine still hard-blocks suicide commands when the live lives count is
    // unknown (no LivesProvider bound) — the conservative default. This setting
    // only takes effect once that lives source is connected.
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // Note: the ailment-handling toggles (the four "Ignore X" @wait gates
    // and the four "do not announce" say-suppression gates) graduated to
    // SpellsSettings — they sit on the Spells tab next to the cure-spell
    // picks they coordinate with. AilmentSyncEngine reads them from there.

    // Master gate for walker trap-disarming. When true (default) the walker
    // routes a trapped exit through the disarm machinery before stepping through
    // — but only when a disarm is actually possible (the local character has the
    // Traps skill, or — once the party-delegation path lands — a party member
    // does). When false the walker walks straight through trapped exits with no
    // disarm attempt. Labeled "Utilize disarm traps if able" in Settings → Other
    // because the "if able" capability check rides on top of this on/off switch.
    public bool UtilizeDisarmTrapsIfAble { get; set; } = true;

    // Caps the disarm-retry loop in the @trap handler — how many disarm trap
    // <dir> attempts we'll make after the trap has been spotted before giving
    // up. Default 5, range 1..50. Damage-aware abort (stop early if the trap
    // fires and we lose HP) ships with the HealthManager wiring.
    public int MaxTrapDisarmAttempts { get; set; } = 5;

    // Location-based auto-equip rules. Each names an item to wear while inside a
    // map area (matched by room number(s) and/or room-name substring); on leaving
    // the area the slot reverts to the current gear set's item. Char-tier; edited
    // in Settings → Other. Read live by Game.Inventory.LocationEquipManager.
    public List<LocationEquipRule> LocationEquipRules { get; set; } = new();

    // When true, the auto-discard engine conceals each excess flagged item with
    // hide <item> instead of drop <item> — it still leaves the pack, but lands
    // out of sight on the ground. Engine hides are excluded from the Transaction
    // history ledger (a discard isn't a stash); manual and stash-room hides still
    // record there. Default false (plain drop). Char-tier; surfaced in
    // Settings → Other. Read live by Game.Inventory.AutoDiscardManager.HideMode.
    public bool HideWhenDiscarding { get; set; }

    // ----- Sneaking ---------------------------------------------------

    // Auto-Sneak stands down while a carried item (a log raft, Stealth -125) leaves
    // the estimated `sn` chance under this many percent, instead of resending `sn`
    // into a refusal in every room. 0 never stands down; 1 stands down only when a
    // sneak can't take at all. Default 15 per user direction (2026-10-07).
    public int SneakStandDownChance { get; set; } = 15;

    // ----- Door / lock handling --------------------------------------

    // Walker's max pick <dir> retries before giving up on a single door. Picking
    // is probabilistic — the skill can fail even when the value meets the door
    // requirement. Default 10 per user direction.
    public int MaxPickAttempts { get; set; } = 10;

    // When true, the walker prefers pick <dir> over bash <dir> on doors where
    // both verbs are viable. Bash is louder and breaks stealth; thieves
    // typically flip this on. Default false (bash-first).
    public bool PicklocksOverBash { get; set; }

    // Walker max sea <dir> retries when revealing a hidden exit ((Hidden)
    // modifier) along the path. Default 20 — mirrors the trap-search cap since
    // it's the same verb, kept separate so the user can tune them independently.
    public int MaxHiddenSearchAttempts { get; set; } = 20;

    // Note: the former "search rooms if item needed" opt-in was retired — arming
    // the per-room `sea` for a route item is now driven by the master Auto-Search
    // toggle (the route picker's "Search en route" card turns it on for the leg),
    // and the shop/give/drop acquisition is armed by that card's forced obtain. So
    // there's no separate setting; see Game.Map.AutoSearchManager (search = master
    // toggle) and AppServices' path-item wiring (posting = per-walk forced obtain).

    // Note: the former "Avoid party-impassable level gates" opt-in graduated to
    // always-on built-in behaviour. Routing a following party around a
    // (Level: MIN to MAX) gate the group can't clear is never something a leader
    // wants OFF (the alternative strands a member), so there's no toggle — the
    // gate check runs whenever this character leads a party. See
    // Game.Remote.PartyLevelTracker (always-on: IsInParty && SelfIsLeader).

    // Follower-side auto-@comeback. When true (default) and a movement-blocking
    // condition (prevents-movement gamedata flag or over-encumbrance) leaves us
    // behind as the party leader walks off, we automatically telepath @comeback
    // <room> to the leader so their party-recovery walk picks us up. When false,
    // the left-behind is still detected but no request is sent — the player
    // handles it manually. Defaults on: the request is a single telepath that
    // moves nothing on our side, so being stranded silently is strictly the
    // worse outcome. Char-tier setting; surfaced in Settings → Other.
    public bool AutoRequestComebackWhenLeftBehind { get; set; } = true;

    // When true, a player flagged "invite to party if seen" is auto-invited only while
    // navigation is running — a walk, loop or auto-lair (running or paused), or an
    // auto-deposit / train trip — not while standing idle. Default false (invite
    // whenever seen). Char-tier; Settings → Other. Pushed into
    // Game.AutoPartyManager.OnlyWhileNavigating.
    public bool AutoInviteOnlyWhileNavigating { get; set; }

    // A walk-to / loop / Auto-Lair started with Run (Auto-Combat off) or Sprint (Sprint
    // Mode) and stopped by hand before it began: true turns Auto-Combat back on /
    // ends Sprint Mode (restoring the autos it turned off) at the stop; false (the
    // default) leaves them off. Char-tier; Settings → Other.
    public bool RunStopRestoresCombat { get; set; }
    public bool SprintStopEndsSprint { get; set; }

    // When true (default) a look at a monster prints its estimated remaining hit
    // points as a yellow line in the terminal scrollback. The status bar's target
    // item is separate: it follows the bar's layout. Saved under the name of the
    // wider "show monster HP lookup" switch this replaced, so a character that had
    // that off keeps the line off. Char-tier; Settings → Other.
    [System.Text.Json.Serialization.JsonPropertyName("ShowMonsterHpLookup")]
    public bool PrintMonsterHpOnLook { get; set; } = true;

    // Ceiling for Monster Intel's "Est. Rounds to Kill" column — a monster
    // whose projected rounds exceed this shows "<cap>+" instead of the raw
    // number (a superboss can otherwise project into the millions, which
    // isn't a meaningful number, just noise). Default 999, range 1..999999.
    // Char-tier; edited directly in Monster Intel (not Settings → Other —
    // changing it shouldn't mean leaving the window).
    public int RoundsToKillCap { get; set; } = 999;

    // Monster Intel "Edit Attacks" picker state, Char-tier, edited in the window.
    // Attack keys are stable strings: "melee:<MudAttackType>" (e.g. "melee:Normal",
    // "melee:Backstab") for the character's usable melee attacks, and "spell:<Short>"
    // (e.g. "spell:mm") for an obtained attack spell. HiddenAttacks lists the attacks
    // hidden from the Your Matchup panel (empty = show all). RoundsAttack is the one
    // attack whose projection fills the master list's "Est. Rounds to Kill" column;
    // null / unset / no-longer-available falls back to the Normal melee attack.
    public List<string> MonsterIntelHiddenAttacks { get; set; } = new();
    public string? MonsterIntelRoundsAttack { get; set; }

    // Monster Intel "Apply Debuffs" picker state, Char-tier. Each entry is a
    // known stat-affecting debuff spell's cast code (its Short); when applied, its
    // AC/DR/Dodge/accuracy/slowness effect is folded onto the selected monster in
    // the matchup what-if. Empty = no debuffs applied.
    public List<string> MonsterIntelAppliedDebuffs { get; set; } = new();

    // Monster Intel "Apply Buffs" picker state, Char-tier: cast codes of the
    // character's own offense buffs (e.g. shadowform) folded into the matchup as if up.
    public List<string> MonsterIntelAppliedBuffs { get; set; } = new();

    // Note: the former per-character verbose toggles (VerboseCombat /
    // VerboseRoomClassifier / VerboseCasting / VerboseCash / VerboseStealth) +
    // WriteCombatRoundTrace lived here briefly. They moved to the Log pane menu
    // as a single "Combat diagnostics" umbrella switch (session-only, not
    // persisted) — see Services/LogDiagnosticState.cs. Verbose tracing is a
    // "while I'm debugging right now" affordance, not a per-character
    // preference, and keeping it off the profile saves it from leaking on
    // between sessions.

    // Note: the run-away (flee) knobs (RunDirection / BreakBeforeFleeing)
    // graduated to CombatSettings — they sit on the Combat tab next to the
    // room thresholds + RunDistance they coordinate with. HealthManager's
    // flee path reads them from there.
}
