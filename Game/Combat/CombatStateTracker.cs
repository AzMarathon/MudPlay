using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Owns PlayerState.InCombat and the Combat gate on MovementCoordinator.
// Subscribes to RoomEntityClassifier.EntitiesObserved for the gate's room-clear
// logic and to combat-line patterns for the PlayerState.InCombat flag.
//
// Gate semantics: the MovementCoordinator.CombatGate is held while the current
// room contains at least one classified monster the user is configured to
// engage. "Engageable" is resolved from the monster's overlay relationship (see
// IsEngageable) — shopkeepers and quest-givers are marked Friend / Neutral and
// don't hold the gate.
//
// Plus a master switch: isAutoAttackEnabled short-circuits the gate to
// never-assert when off, so a fresh character (default
// CombatSettings.MasterAutoAttackEnabled = false) walks through every room
// unimpeded until the user opts in.
//
// PlayerState.InCombat flips true on CombatStatus Engaged OR UserHits OR MobHits
// OR MobMisses. It does NOT flip false on CombatStatus Off — see
// OnCombatStatus. The authoritative end-of-combat signal is the room going clear
// of engageable monsters (OnEntitiesObserved's "room cleared" branch).
public sealed class CombatStateTracker : IDisposable
{
    // Identifier this tracker uses when asserting / clearing the Combat gate.
    // Surfaces in MovementCoordinator.History + [Gate] log lines.
    public const string AsserterName = "CombatStateTracker";

    // LogService category for tracker-emitted rows.
    public const string LogCategory = "CombatGate";

    // Idle-stall watchdog. The Combat gate clears authoritatively only when a
    // room re-display shows the room empty of engageable monsters
    // (OnEntitiesObserved's "room cleared" branch). Normally the death of the
    // last monster forces that re-display (CombatManager.NoteUnattributedDeath /
    // OnCommandNoEffect send a bare CR). But when a final kill routes through
    // neither path, the empty room emits no "Also here:" line so the classifier
    // fires no observation at all — the gate stays asserted and the walker hangs
    // "fighting" an empty room until a manual redisplay.
    //
    // The watchdog runs off the 1s heartbeat (TickEngine.HeartbeatElapsed) and is
    // single-phase: once the gate has been held for IdleStallThreshold with zero
    // combat activity, it BOTH sends a benign resync CR AND force-clears the gate
    // in the same tick. The clear is optimistic — 6s of total silence while the
    // gate is held means an empty room (a live fight emits a line every 5s
    // round), so releasing the walker is correct. The CR is a safety probe: if a
    // real monster lingered (a laggy >6s round), its re-displayed "Also here:"
    // re-asserts the gate a beat later, so an over-eager clear self-heals. Driving
    // this off the 1s heartbeat rather than the coarse 5s combat tick is what
    // keeps total recovery at ~6s instead of quantizing it up to ~10-15s.
    private static readonly TimeSpan IdleStallThreshold = TimeSpan.FromSeconds(6);

    private readonly MovementCoordinator _coordinator;
    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay>? _resolveOverlay;
    private readonly PlayerState _state;
    private readonly Func<bool> _isAutoAttackEnabled;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _mobAttacksSub;
    private readonly IDisposable _combatStatusSub;

    private bool _gateAsserted;
    private bool _anyNpcPresent;
    private bool _hostilePresent;
    private bool _disposed;

    // Idle-stall watchdog stamp: the last sign of a live fight (a combat
    // damage/miss line or a room observation still holding a monster). The
    // watchdog fires once this goes IdleStallThreshold stale with the gate held.
    private DateTimeOffset _lastCombatActivityAt = DateTimeOffset.MinValue;

    // Watchdog diagnostics: when the gate was asserted, and what last refreshed
    // the activity stamp. Logged when the watchdog fires so a capture can tell a
    // post-kill idle release (last activity = a combat line) from a pattern gap
    // (last activity = only the room-entry observation — our attack lines never
    // matched, so they never refreshed the stamp).
    private DateTimeOffset _gateAssertedAt;
    private string _lastCombatActivityDesc = "none";

    private Func<bool>? _clearWhenSeenHidden;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<int, bool>? _hasSeeHidden;
    private Func<int, bool>? _canEngage;
    // RawName → did the user hand-engage this passive neutral? Wired from CombatManager
    // so the walker gate (HasEngageableHostiles) also holds while the user's manually-
    // engaged neutral is still alive, matching CombatManager's attack takeover.
    private Func<string, bool>? _isUserEngagedInstance;
    private bool _seeHiddenClearLatch;

    private Action<byte[]>? _wireSender;
    private Func<bool>? _breakBeforeRunning;

    // CombatSettings.MinMonstersInRoom / MaxMonstersInRoom reader — see
    // SetMonsterCountWindow. Null (unwired) fails open: the gate asserts for any
    // actionable hostile regardless of room population, matching this tracker's
    // behavior before the window existed.
    private Func<CombatSettings>? _readSettings;

    // Reports whether a movement engine (walker / loop / auto-lair) is
    // currently attached and driving us through rooms — see
    // SetMovementActiveGate. The Min/Max window below only makes sense while
    // something is actually trying to move us past this room; standing here
    // with nowhere to go (no engine attached) should hold the gate and fight
    // regardless of population. Null (unwired) fails open to "active" — the
    // window applies unconditionally, matching this tracker's behavior before
    // this gate existed.
    private Func<bool>? _isMovementActive;

    // Reports whether we're standing in a too-dark room (RoomTracker.IsInDarkRoom).
    // Gates the idle-stall watchdog's resync CR: a CR in the dark re-emits no
    // "Also here:" line (nothing to re-observe) AND its "you can't see anything"
    // reply is dead-reckoned by RoomTracker as a false confirmation of the
    // movement loop's in-flight step, so we skip the probe and just clear the stuck
    // gate. null until wired → fail-open (the resync CR sends as before).
    private Func<bool>? _isInDarkRoom;

    // True while the room currently contains at least one engageable
    // (Enemy-relationship, killable) monster. Drives the
    // MovementCoordinator.CombatGate + lets HealthManager gate the rest decision
    // so we don't try to rest while a mob is here — every combat round would
    // otherwise break rest and we'd never actually recover. Clears
    // authoritatively when an Also-Here observation shows no engageable monsters
    // (room cleared).
    public bool HasEngageableHostiles => _gateAsserted;

    // True while the room holds at least one engageable (Enemy-relationship)
    // monster — the same per-entity predicate that drives the gate, but WITHOUT
    // the auto-attack master-switch short-circuit. HasEngageableHostiles reports
    // false whenever auto-attack is off (a manual player never asserts the gate);
    // this reports the raw danger regardless, so the emergency-hangup gate can ask
    // "is a hostile in the room?" for a character who isn't auto-fighting.
    public bool HasHostileMonster => _hostilePresent;

    // True while the current room contains at least one NPC / monster of any
    // relationship (Enemy, Friend, Neutral — shopkeepers and quest-givers
    // included). Sneak cannot be established while any NPC is present, so the
    // StealthManager pre-move hook consults this to suppress a doomed sn. Updated
    // on every Also-Here observation, independent of the auto-attack gate (which
    // only reacts to engageable hostiles).
    public bool HasRoomNpc => _anyNpcPresent;

    // True while a combat-off "clear hostiles when seen Hidden" force-clear is
    // latched for the current room. A stealth runner (AutoSneak on) sprinting a
    // route with combat OFF that hits a room holding a SeeHidden monster can't
    // re-sneak there; running onward would drag and stack monsters across rooms,
    // lethal when solo. When CombatSettings.ClearHostilesWhenSeenHidden is on,
    // this latches on entry to such a room — holding the Combat gate (so the
    // walker actually stops) until every engageable hostile is gone.
    // CombatManager reads this to engage despite combat-off.
    public bool SeeHiddenClearActive => _seeHiddenClearLatch;

    // Fires when a confirmed room change happens while the combat gate is held —
    // an in-flight move carried us out of a room where we'd engaged an
    // actionable hostile before it died. The walker subscribes and halts so the
    // route doesn't keep going deeper past a fight we committed to. The argument
    // is a human-readable reason for the log / walk event. (The gate itself is
    // still cleared for the new room — see OnEntitiesObserved's RoomChange arm.)
    public event Action<string>? EngagedTargetAbandoned;

    // Fires when a room clears of engageable hostiles and combat truly ends, so a
    // stealth consumer can drop the sneak/hide the fight spent. Attacking reveals
    // you (the surprise opener is one-shot — see GAME_MECHANICS), but the stealth
    // FSM has no line-driven signal for it, so IsSneaking stays stale-true after a
    // kill. StealthManager subscribes and resets to Idle; raised BEFORE the Combat
    // gate releases so the walker's pre-move re-sneak sees a clean state and can
    // re-establish stealth for the step out (report stock-20260730-163044).
    public event Action? CombatSpentStealth;

    // Fires when combat is FORCE-cleared (the idle-stall watchdog, or manual Reset
    // States) rather than via a normal room-clear. The normal end-of-fight flushes
    // any pickup deferred "until combat clears" off the ensuing clean room re-look;
    // a force-clear produces no such observation, so a deferred ground-cash / item
    // collect would otherwise strand the Acquisition gate and wedge the walker
    // (report paradigm-20260814-131551). Consumers re-run their post-combat flush
    // against the now-cleared gate (HasEngageableHostiles is already false here).
    public event Action? CombatForceCleared;

    public CombatStateTracker(
        MessageRouter router,
        MovementCoordinator coordinator,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        PlayerState state,
        Func<bool> isAutoAttackEnabled,
        LogService? log = null)
        : this(router, coordinator, classifier, monsters, state,
               isAutoAttackEnabled, resolveOverlay: null, log) { }

    // Construct with a per-monster overlay resolver so the engageable predicate
    // matches CombatManager (Relationship-based). Without it, the tracker falls
    // back to "every monster engageable" which can spuriously assert the Combat
    // gate against shopkeepers (CombatManager would skip them but the walker
    // would still pause). AppServices wires the same delegate it gives
    // CombatManager so the two stay in sync.
    public CombatStateTracker(
        MessageRouter router,
        MovementCoordinator coordinator,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        PlayerState state,
        Func<bool> isAutoAttackEnabled,
        Func<int, MonsterOverlay>? resolveOverlay,
        LogService? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isAutoAttackEnabled);

        _coordinator = coordinator;
        _classifier  = classifier;
        _monsters    = monsters;
        _resolveOverlay = resolveOverlay;
        _state       = state;
        _isAutoAttackEnabled = isAutoAttackEnabled;
        _log         = log;
        _now         = clock ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _userHitsSub      = router.Subscribe(KnownPatterns.UserHits,      OnAnyCombatLine);
        _mobHitsSub       = router.Subscribe(KnownPatterns.MobHits,       OnAnyCombatLine);
        _mobMissesSub     = router.Subscribe(KnownPatterns.MobMisses,     OnAnyCombatLine);
        // Broad mob-attacks-us line — catches the per-round swings that MobHits /
        // MobMisses miss on realms whose melee carries no damage number or "at
        // you", so an active fight keeps refreshing the idle-stall stamp instead
        // of tripping the watchdog (report stock-20260730-190736).
        _mobAttacksSub    = router.Subscribe(KnownPatterns.MobAttacksYou, OnAnyCombatLine);
        _combatStatusSub  = router.Subscribe(KnownPatterns.CombatStatus,  OnCombatStatus);
    }

    // Wire the combat-off "clear hostiles when seen Hidden" override:
    // clearWhenSeenHidden reads CombatSettings.ClearHostilesWhenSeenHidden,
    // isAutoSneakEnabled reports whether the character is stealthing its route
    // (AutoSneak auto-mode on), and hasSeeHidden reports whether a monster Number
    // carries SeeHidden (SeeHiddenIndex). With all wired, entering a room that
    // breaks a stealth runner's sneak latches a force-clear (see
    // SeeHiddenClearActive). Until set, the override stays dormant and the gate
    // behaves exactly as before.
    public void SetSeeHiddenClearGate(
        Func<bool> clearWhenSeenHidden,
        Func<bool> isAutoSneakEnabled,
        Func<int, bool> hasSeeHidden)
    {
        ArgumentNullException.ThrowIfNull(clearWhenSeenHidden);
        ArgumentNullException.ThrowIfNull(isAutoSneakEnabled);
        ArgumentNullException.ThrowIfNull(hasSeeHidden);
        _clearWhenSeenHidden = clearWhenSeenHidden;
        _isAutoSneakEnabled = isAutoSneakEnabled;
        _hasSeeHidden = hasSeeHidden;
    }

    // Wire the actionability gate: canEngage reports whether a monster Number is
    // one we can actually kill (a weapon can hit it OR an eligible attack spell
    // can land — see CombatManager.CanEngageMonster). With it wired, the walker
    // gate is held only while at least one engageable hostile is actionable; a
    // room whose remaining hostiles are all un-actionable releases the gate so
    // the walker moves past instead of standing there unable to win. Until set,
    // every engageable hostile counts as actionable (fail-open — the gate behaves
    // exactly as before).
    public void SetActionabilityGate(Func<int, bool> canEngage)
    {
        ArgumentNullException.ThrowIfNull(canEngage);
        _canEngage = canEngage;
    }

    // Wire CombatManager's per-instance "user hand-engaged this passive neutral" set so
    // the walker gate treats that neutral as an engageable hostile too. Without it the
    // gate would release (the neutral is un-tagged, so not engageable on its own) and the
    // walker could stroll off mid-fight while CombatManager is still killing it. Until
    // set, no instance is user-engaged (fail-open — behaves exactly as before).
    public void SetUserEngagedInstanceGate(Func<string, bool> isUserEngagedInstance)
    {
        ArgumentNullException.ThrowIfNull(isUserEngagedInstance);
        _isUserEngagedInstance = isUserEngagedInstance;
    }

    // Wire the CombatSettings.MinMonstersInRoom / MaxMonstersInRoom reader so
    // this tracker's gate agrees with CombatManager's own min/max skip
    // (OnEntitiesObserved's "min/max gate skip" branch) on whether a room is
    // fightable. Without this the two disagreed: CombatManager silently
    // declines to engage a room outside the window, but this tracker — which
    // only reasoned about actionability, not room population — still held the
    // walker gate for ANY actionable hostile regardless of count. That
    // deadlocked the character in a too-crowded room: combat refusing to
    // fight, the walker unable to leave, standing there absorbing hits from
    // every monster in the room with no recourse. A room outside the window
    // now takes the SAME "un-actionable, move past" path as a room full of
    // monsters no weapon or spell can hit. Until set, every engageable
    // hostile counts regardless of room population (fail-open — the gate
    // behaves exactly as before this fix). See also SetMovementActiveGate —
    // the window only applies while something is actually moving us through.
    public void SetMonsterCountWindow(Func<CombatSettings> readSettings)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        _readSettings = readSettings;
    }

    // Wire the movement-active probe (EngineRecoveryGate.AttachedEngine is not
    // null) — see _isMovementActive. Until set, the Min/Max window (once
    // SetMonsterCountWindow is also wired) applies unconditionally.
    public void SetMovementActiveGate(Func<bool> isMovementActive)
    {
        ArgumentNullException.ThrowIfNull(isMovementActive);
        _isMovementActive = isMovementActive;
    }

    // Wire path for the break-before-run disengage. Bound at connect time (the
    // same gate-wrapped engineSend every other engine receives). Until set, the
    // break-before-run step no-ops.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the dark-room probe (RoomTracker.IsInDarkRoom). With it set, the
    // idle-stall watchdog skips its resync CR while we can't see (see
    // _isInDarkRoom / OnCombatTick). Until set, the resync CR sends unconditionally.
    public void SetDarkRoomProbe(Func<bool> isInDarkRoom)
    {
        ArgumentNullException.ThrowIfNull(isInDarkRoom);
        _isInDarkRoom = isInDarkRoom;
    }

    // Wire the CombatSettings.BreakBeforeFleeing reader. When set and true,
    // toggling auto-attack OFF mid-fight sends `break` before the Combat gate
    // releases the walker, so the disengage lands ahead of the walker's next
    // move. Until set, no break is ever sent (behaviour unchanged).
    public void SetBreakBeforeRunGate(Func<bool> breakBeforeRunning)
    {
        ArgumentNullException.ThrowIfNull(breakBeforeRunning);
        _breakBeforeRunning = breakBeforeRunning;
    }

    // Re-evaluate the gate + InCombat the instant the auto-attack master toggle
    // flips, rather than waiting for the next room observation. Toggling
    // auto-attack OFF mid-round otherwise left the walker gate asserted (walker
    // stalled) and InCombat stuck true (no rest) until the user forced a room
    // re-display. Re-running the last observation applies the new toggle state
    // at once; it never sends an attack (that's CombatManager's own subscriber).
    public void OnAutoAttackChanged()
    {
        if (_disposed) return;

        // Turning auto-attack OFF mid-fight releases the Combat gate (in the
        // re-run observation below), letting the walker resume. When the user
        // wants a clean disengage first (CombatSettings.BreakBeforeFleeing), send
        // `break` before that release so it lands ahead of the walker's next move.
        // Gate on _gateAsserted so this only fires when we were actually holding
        // the walker for a fight, and on InCombat so a routine walk never breaks.
        // Fires once — this handler runs only on the toggle transition, not on
        // every room observation.
        if (!_isAutoAttackEnabled()
            && _gateAsserted
            && _state.InCombat
            && (_breakBeforeRunning?.Invoke() ?? false))
        {
            _log?.Info(LogCategory,
                "auto-attack off mid-combat — sending break before releasing walker (BreakBeforeFleeing)");
            SendCommand("break");
        }

        if (_classifier.Current is { } obs) OnEntitiesObserved(obs);
    }

    // Idle-stall watchdog, driven by the 1s heartbeat (TickEngine's
    // HeartbeatElapsed). Rescues two stall shapes after IdleStallThreshold of
    // zero combat activity — a live fight emits a line every 5s round, so total
    // silence means the fight is over and the room is empty:
    //   1. Gate asserted (auto-attack ON) — the classic walker hang: the empty
    //      re-display carried no "Also here:" line for the classifier to observe,
    //      so the gate stayed held over an empty room.
    //   2. InCombat stuck true with the gate NOT asserted (auto-attack OFF) — an
    //      incoming combat line latched InCombat, but with the gate never
    //      asserted the ONLY path that clears InCombat (OnEntitiesObserved's
    //      combat-off branch) runs solely on a room re-display that idle play
    //      never produces. InCombat then hangs, silencing rest AND bless (bless
    //      refuses to fire in combat), which reads to the user as "auto-bless
    //      stopped when I turned auto-combat off."
    // Single-phase recovery for both: send a benign resync CR (the safety probe)
    // AND force the tracker idle in the same tick. If a monster actually lingered
    // — a laggy >6s round — the CR's re-displayed "Also here:" re-observes a beat
    // later, so the optimistic clear self-heals. Running on the 1s heartbeat
    // lands this in ~6s total.
    public void OnCombatTick()
    {
        if (_disposed) return;
        if (_wireSender is null) return;
        // Nothing to rescue unless the gate is held OR InCombat is stuck true.
        // With auto-attack off InCombat can hang with the gate clear, so the
        // gate alone is not a sufficient trigger — see shape 2 above.
        if (!_gateAsserted && !_state.InCombat) return;
        if (_now() - _lastCombatActivityAt < IdleStallThreshold) return;

        double idleSec = (_now() - _lastCombatActivityAt).TotalSeconds;
        // Skip the resync CR while we can't see: a dark room re-emits no
        // "Also here:" to re-observe, and its "you can't see anything" reply is
        // dead-reckoned as a false confirm of the movement loop's in-flight step.
        // The combat state still clears optimistically either way.
        bool dark = _isInDarkRoom?.Invoke() == true;
        string held = _gateAsserted
            ? $"combat gate held {(_now() - _gateAssertedAt).TotalSeconds:F1}s"
            : "InCombat stuck (auto-attack off)";
        _log?.Info(LogCategory,
            $"{held}, idle {idleSec:F1}s since last activity "
            + $"({_lastCombatActivityDesc}) — no combat activity for the stall window, "
            + (dark
                ? "room empty; dark room, skipping resync CR and clearing stuck combat state"
                : "room empty; resyncing and clearing stuck combat state"));
        if (!dark)
            _wireSender(Encoding.Latin1.GetBytes("\r"));
        ResetCombatState("idle-stall watchdog: no combat activity — room empty");
    }

    private void SendCommand(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        // Single pass over the room: ANY monster (friendly or hostile)
        // blocks sneak (NPC-presence signal, independent of the gate);
        // engageable hostiles drive the gate; a SeeHidden occupant arms
        // the combat-off clear override.
        _anyNpcPresent = false;
        int targetable = 0;
        int actionable = 0;
        int attacking = 0;
        string? first = null;
        bool roomHasSeeHidden = false;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            _anyNpcPresent = true;
            bool engageable = IsEngageable(e);
            if (engageable)
            {
                targetable++;
                if (IsActionable(e))
                {
                    actionable++;
                    first ??= e.ResolvedName;
                }
            }
            // On-sight attackers only (Enemy relationship) — a passive KOS-neutral is
            // engageable but never attacks until we hit it, so it doesn't count here.
            if (IsAttackingHostile(e)) attacking++;
            LogMonsterDecision(e, engageable);
            if (!roomHasSeeHidden && e.MonsterNumber is int n
                && _hasSeeHidden?.Invoke(n) == true)
            {
                roomHasSeeHidden = true;
            }
        }

        // Raw hostile presence, updated on every observation ahead of the
        // auto-attack branches below so it stays accurate even when the gate
        // itself is short-circuited off (manual player). Read by the
        // emergency-hangup gate + the health rest-block. Enemy-relationship
        // (on-sight attackers) only — a passive KOS-neutral is engageable but not a
        // threat until we hit it, so it must not read as "a hostile is here."
        _hostilePresent = attacking > 0;

        // See-hidden force-clear override for stealth runners — applies with
        // auto-attack ON or OFF (hoisted above the split). A stealth character
        // (AutoSneak on) sprinting a walk-to route enters a room whose SeeHidden
        // monster breaks sneak; running onward would drag/stack the room's
        // monsters across rooms (lethal solo). With the toggle on we force-clear
        // the WHOLE room — holding the walker gate here, and (via CombatManager's
        // SeeHiddenClearActive read) bypassing the Min/Max monster gate — so the
        // route can re-sneak and go back to avoiding combat. Once latched, hold
        // until every engageable hostile is gone, even after the SeeHidden monster
        // itself dies.
        bool seeHiddenArm = _clearWhenSeenHidden?.Invoke() == true
                            && _isAutoSneakEnabled?.Invoke() == true
                            && roomHasSeeHidden;
        if (_seeHiddenClearLatch || seeHiddenArm)
        {
            // Hold only while something here is actually killable. If the
            // remaining hostiles are all un-actionable, standing to clear a room
            // we can't clear would deadlock the runner — release and let the walker
            // move past (a fight we can't win is worse than a broken sneak).
            if (actionable > 0)
            {
                _seeHiddenClearLatch = true;
                AssertGate("seehidden clear (force-clear room)");
                return;
            }
            _seeHiddenClearLatch = false;   // room cleared / un-actionable — release.
        }

        if (!_isAutoAttackEnabled())
        {
            // Auto-attack off and no see-hidden override → never hold the gate.
            // Defensive clear in case it was asserted just before toggle.
            ClearGate("auto-attack disabled");
            // A room clear of engageable hostiles is the authoritative
            // out-of-combat signal even with auto-attack off — otherwise
            // InCombat stays stuck true and HealthManager never rests
            // (CombatStatus=Off is unreliable, see OnCombatStatus). A hostile
            // still here keeps InCombat true so we don't rest next to a mob.
            if (targetable == 0 && _state.InCombat) _state.InCombat = false;
            return;
        }

        // Auto-attack on — the normal gate owns pausing.
        bool withinCountWindow = IsWithinMonsterCountWindow(targetable);
        if (actionable > 0 && withinCountWindow)
        {
            // A fresh observation still holding a killable monster is a live
            // fight — refresh the watchdog stamp (AssertGate early-outs when the
            // gate's already held, so stamp here regardless so a slow-but-real
            // fight never trips the stall watchdog).
            _lastCombatActivityAt = _now();
            _lastCombatActivityDesc = "room-entry hostile";
            string reason = first is null
                ? $"room-entry actionable={actionable}/{targetable}"
                : $"room-entry actionable={actionable}/{targetable} first={first}";
            AssertGate(reason);
        }
        else
        {
            // A confirmed room change (synthetic empty wipe) while the gate is
            // held means an in-flight move carried us OUT of a room where we'd
            // engaged an actionable hostile — we didn't kill it (a real kill
            // clears the gate on the Death observation first), we left it. That's
            // an abandoned fight, not a room we cleared by winning: signal the
            // walker to halt so it doesn't keep walking the route deeper past a
            // fight we committed to. We still clear the gate below — the new room
            // is genuinely empty of the old target and holding would deadlock (an
            // empty room emits no further observation to release it); if the
            // monster followed, its arrival observation re-asserts within
            // milliseconds.
            if (obs.Source == RoomObservationSource.RoomChange && _gateAsserted)
                EngagedTargetAbandoned?.Invoke("left a room with an engaged target still alive");

            // Room is now clear of engageable monsters → combat truly ended. This
            // is the authoritative "we're out of combat" signal; CombatStatus=Off
            // is unreliable (the server emits it when we cast a spell mid-round,
            // with the mob still alive — see OnCombatStatus). Only a room confirmed
            // clear of engageable hostiles flips it, so HealthManager never rests
            // next to a live mob.
            //
            // Ordering is load-bearing — each step feeds a synchronous downstream
            // reaction, so the sequence matters:
            //   1. Drop _gateAsserted first. HasEngageableHostiles is derived from
            //      it, and step 2's InCombat=false fires HealthManager.Evaluate
            //      synchronously; if the gate still read asserted there, the rest
            //      is blocked as "hostile present" and deferred to the next prompt
            //      tick — a multi-second stall after the room clears (report
            //      stock-20260730-184622).
            //   2. Flip InCombat false (+ drop the stealth the fight spent) BEFORE
            //      the coordinator gate releases the walker, so the pre-move
            //      re-sneak sees out-of-combat (report stock-20260730-163044).
            //      CombatSpentStealth only fires when we actually engaged — a pure
            //      walk-past of an un-actionable room never broke our sneak.
            //   3. Release the coordinator Combat gate — resumes the walker + its
            //      pre-move hook now that both InCombat and HasEngageableHostiles
            //      read clear. Inlined rather than via ClearGate so InCombat can
            //      flip between the flag drop (1) and the coordinator release (3).
            // targetable>0 means hostiles remain but either none are killable, or
            // the room's population falls outside the configured Min/Max window
            // while something is actively moving us through — either way, release
            // and move past (the move-past rule); the "room cleared" wording is
            // the genuine empty-room case.
            string clearReason = targetable switch
            {
                > 0 when !withinCountWindow =>
                    $"room outside Min/Max monster window: {targetable} hostile(s) — moving on",
                > 0 => $"room un-actionable: {targetable} hostile(s), none hittable — moving on",
                _ => "room cleared",
            };
            bool wasAsserted = _gateAsserted;
            _gateAsserted = false;                                        // (1)
            if (_state.InCombat)
            {
                _state.InCombat = false;                                  // (2)
                CombatSpentStealth?.Invoke();
            }
            if (wasAsserted)                                              // (3)
                _coordinator.ClearGate(MovementCoordinator.CombatGate, AsserterName, clearReason);
        }
    }

    // Mirrors CombatManager.OnEntitiesObserved's own min/max skip so the two
    // never disagree on whether a room is worth fighting — see
    // SetMonsterCountWindow. The window itself only applies while a movement
    // engine is actually attached (SetMovementActiveGate) — standing here idle
    // with nowhere to go should fight regardless of population. Doesn't mirror
    // the Party-tab MaxMonstersWhenPartying override CombatManager applies
    // while partied (this tracker has no PartySettings reader); a partied cap
    // tighter than the solo Combat-tab Max could still leave the two
    // disagreeing in that one case. Unwired, not moving, or a misconfigured
    // Min > Max, all fail open (every count passes — gate behaves exactly as
    // before this fix).
    private bool IsWithinMonsterCountWindow(int targetable)
    {
        if (_readSettings is null) return true;
        if (!(_isMovementActive?.Invoke() ?? true)) return true;
        CombatSettings settings = _readSettings();
        int min = Math.Max(0, settings.MinMonstersInRoom);
        int max = settings.MaxMonstersInRoom > 0 ? settings.MaxMonstersInRoom : int.MaxValue;
        if (min > max) return true;                       // misconfig — no gate
        return targetable >= min && targetable <= max;
    }

    // Engageable = Enemy (the default for a resolved-but-untagged monster) OR a Neutral
    // flagged KillOnSight — see MonsterEngagement. Shopkeepers / quest-givers / friendly NPCs
    // are marked Friend / Neutral / Hangup in the overlay seed; a monster whose Number resolves
    // but whose overlay is missing is treated as fightable so the engine doesn't sit through a
    // respawn just because the data table is missing a DeathLine (152 of 1100 stock monsters
    // ship with empty DeathLine — acid slime, etc.).
    //
    // A monster the classifier CANNOT resolve to a Number is NOT engaged — we never proactively
    // hit something we can't identify, so a friendly / neutral NPC whose name didn't pin to its
    // record (a greet-only "old man") is left alone rather than defaulting to a fightable enemy.
    // This is a fail-CLOSED distinct from the resolved-but-untagged fail-open above; the
    // reactive InCombat path still fights back if such a mob actually attacks us.
    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return false;
        // A passive neutral the user hand-engaged fights like a hostile until dead, so it
        // holds the walker gate the same way an enemy does — even before its overlay says
        // so, and regardless of the resolver being wired.
        bool userEngaged = _isUserEngagedInstance?.Invoke(e.RawName) == true;
        if (_resolveOverlay is null) return true;        // legacy ctor — engage everything
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return MonsterEngagement.IsEngageable(overlay, userEngaged);
    }

    // On-sight attacker: an Enemy-relationship monster (the default for un-tagged
    // mobs). These hit us every round we share the room, so they block resting. A
    // Neutral — even a KillOnSight one we intend to fight — never attacks first, so
    // it is NOT an attacking hostile (it only hits back once we engage it, which the
    // InCombat flag then reflects). Backs HasAttackingHostile / the rest-block.
    private bool IsAttackingHostile(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;   // unknown → assume enemy (fail-safe)
        if (_resolveOverlay is null) return true;
        MonsterOverlay overlay;
        try { overlay = _resolveOverlay(n) ?? new MonsterOverlay(); }
        catch { return true; }
        return (overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy;
    }

    // Per-monster Combat-level trace so a log read explains WHY the engine engaged or skipped
    // each room occupant: the detection (name → resolved record Number), its user Relationship +
    // Kill-on-sight, and the resulting engage / skip decision. Exactly what a "why did it attack
    // the friendly NPC?" report needs. Combat severity — verbose per-observation detail.
    private void LogMonsterDecision(RoomEntity e, bool engageable)
    {
        if (_log is null) return;

        string record = e.MonsterNumber is int n ? $"#{n}" : "unresolved";
        string relationship = "Enemy (default)";
        bool killOnSight = false;
        if (e.MonsterNumber is int mn && _resolveOverlay is not null)
        {
            MonsterOverlay? overlay;
            try { overlay = _resolveOverlay(mn); } catch { overlay = null; }
            relationship = (overlay?.Relationship ?? MonsterRelationship.Enemy).ToString();
            killOnSight = overlay?.KillOnSight == true;
        }

        _log.Combat(LogCategory,
            $"detected '{e.ResolvedName}' ({record}) · relationship {relationship}"
            + (killOnSight ? " · kill-on-sight" : "")
            + $" → {(engageable ? "engage" : "skip")}");
    }

    // Actionable = we can actually kill it (a weapon can hit it OR an eligible
    // attack spell can land). Fail-open: an unwired gate, an unknown monster
    // Number, or a resolver exception all count as actionable so a thin data set
    // never strands the walker. The caller only invokes this for entities that
    // already passed IsEngageable.
    private bool IsActionable(RoomEntity e)
    {
        if (_canEngage is null) return true;             // unwired → fail open
        if (e.MonsterNumber is not int n) return true;   // unknown number → fail open
        try { return _canEngage(n); }
        catch { return true; }
    }

    private void AssertGate(string reason)
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _gateAssertedAt = _now();
        _coordinator.AssertGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    private void ClearGate(string reason)
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.CombatGate, AsserterName, reason);
    }

    // Manual escape hatch (Reset States). Force the tracker back to an idle,
    // out-of-combat state: release the Combat gate, drop the combat-off seehidden
    // latch, and clear InCombat. The case this rescues: a stale roster (a fallback
    // death whose forced re-display never wiped the room) leaves the gate asserted
    // and the walker parked "fighting" an empty room with no in-game line left to
    // release it — the idle-stall watchdog keeps re-displaying but the roster
    // never clears. Reset States clearing conditions alone left this stuck, so it
    // routes here too. The next genuine room observation re-derives presence from
    // scratch, so a hostile that really remains re-asserts within a round.
    public void ResetCombatState(string reason)
    {
        ClearGate(reason);
        _seeHiddenClearLatch = false;
        if (_state.InCombat) _state.InCombat = false;
        _log?.Info(LogCategory, $"combat state force-cleared — {reason}");
        CombatForceCleared?.Invoke();   // fired last: the gate is now down, so a deferred collect can flush
    }

    // ----- InCombat plumbing ----------------------------------------

    private void OnAnyCombatLine(MatchResult _)
    {
        // A damage/miss line is proof the fight is live — refresh the
        // watchdog's activity stamp so it never fires mid-fight.
        _lastCombatActivityAt = _now();
        _lastCombatActivityDesc = "combat line";
        if (!_state.InCombat) _state.InCombat = true;
    }

    // The combat manager is holding engagement of a passive KillOnSight neutral so
    // the health engine can rest first — a neutral never attacks until we hit it, so
    // sitting in the room with un-engaged neutrals is safe. Clearing InCombat lets
    // the rest fire; the Combat gate stays asserted (an engageable target remains) so
    // the walker keeps holding. Re-engaging re-sets InCombat via the neutral's first
    // hit back. This tracker owns InCombat, so the write lives here.
    public void ClearInCombatForRecoveryHold()
    {
        if (_state.InCombat) _state.InCombat = false;
    }

    private void OnCombatStatus(MatchResult match)
    {
        // (?<status>Engaged|Off) capture in DefaultPatterns.
        if (match.Groups.Count == 0) return;
        string status = match.Groups[0];

        // Only Engaged matters — when the server says we're now in
        // combat, mirror that. We do NOT flip to false on
        // *Combat Off* because the server emits Off whenever
        // auto-attack stops for ANY reason, including casting a
        // spell mid-round with the mob still alive. Doing so would
        // let HealthManager start resting while a hostile is right
        // next to us. The authoritative end-of-combat signal is the
        // room going clear of engageable monsters — handled by
        // OnEntitiesObserved's "room cleared" branch.
        if (string.Equals(status, "Engaged", StringComparison.OrdinalIgnoreCase))
        {
            if (!_state.InCombat) _state.InCombat = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _mobAttacksSub.Dispose();
        _combatStatusSub.Dispose();
    }
}
