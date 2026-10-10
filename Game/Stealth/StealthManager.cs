using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Stealth;

// Stealth state tracker. Owns PlayerState.IsSneaking and PlayerState.IsHidden.
// Other engines (CombatManager's pre-attack suppression, CastingDirector's buff
// gate) read those flags to protect the backstab window.
//
// FSM transitions are line-driven (no timers). The observed signals and their
// meanings, per the live MajorMUD sneak sequence:
//   * UserSneakInitiate (clean "Attempting to sneak..." with no failure suffix)
//     is the server ACK that the sneak attempt took — the character is armed and
//     may now move. Establishes StealthState.Sneaking + IsSneaking=true.
//   * UserSneakFailed ("Attempting to sneak...You don't think you're sneaking.")
//     is a soft rejection: the attempt didn't take and must be retried by
//     resending sn. When auto-sneak owns the loop we resend (capped at
//     MaxSneakRetries); otherwise we settle on StealthState.Failed for the caller
//     to retry.
//   * UserSneaking ("Sneaking...", emitted on each room entry while sneak holds)
//     is the post-move confirmation that we entered the new room unseen.
//     Re-establishes StealthState.Sneaking and re-arms the silent-loss watchdog
//     for the room.
//   * UserNotSneaking ("You make a sound as you enter the room!") →
//     StealthState.Idle + IsSneaking=false. Loud loss.
//   * UserCantSneak ("You may not sneak right now!") → StealthState.Failed. It's
//     the game's post-combat cooldown and clears in seconds: with auto-sneak on we
//     hold movement (MovementCoordinator.SneakCooldownGate) and retry sn until it
//     takes or SneakCooldownCap passes (report paradigm-20260926-233357). It's also
//     the answer while a monster shares the room — when one has followed us in, the
//     walk carries on unsneaked instead (see "Followed" below).
//
// Hide is a separate, thinner FSM because its success is NOT self-observable —
// the server runs a hide check but never reports that it landed:
//   * UserHideInitiate (bare "Attempting to hide...") is treated as
//     OPTIMISTICALLY hidden: StealthState.Hidden + IsHidden=true. The backstab
//     surprise-round resolver (CombatManager) confirms or denies after the opener
//     swings — a real hide lands the surprise, a false one whiffs and flees.
//   * UserHideFailed ("...You don't think you are hidden.") is the one ground-truth
//     failure line → drop the optimistic hidden state.
//   * A move (NoteRoomChanged) breaks hide — you can't move while hidden.
// Auto-hide (NoteIdleOpportunity) is suppressed while in a party: a hidden member
// falls off the room's Also-here line and can't be targeted by party heals/buffs.
//
// Silent-loss detection: in MajorMUD, sneak silently breaks when a move is
// observed (or a stealth-breaking action fires) without the "Sneaking..." line.
// The watchdog flag _sneakConfirmedThisRoom is set on every positive signal
// (clean initiate OR "Sneaking...") and cleared on NoteRoomChanged. If we
// believed we were sneaking but the new room never re-confirmed, we treat that as
// a silent loss and drop the flag — preventing the engine from thinking we're
// still hidden when CombatManager is about to swing.
public sealed class StealthManager : IDisposable
{
    // LogService category — appears as [Stealth] rows per FSM transition +
    // silent-loss detection.
    public const string LogCategory = "Stealth";

    // Max consecutive sn resends after a soft sneak rejection ("You don't think
    // you're sneaking.") before the auto-sneak loop gives up for this room. Reset
    // on every positive sneak signal + room change.
    private const int MaxSneakRetries = 10;

    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly IDisposable _sneakingSub;
    private readonly IDisposable _notSneakingSub;
    private readonly IDisposable _sneakInitiateSub;
    private readonly IDisposable _sneakFailedSub;
    private readonly IDisposable _cantSneakSub;
    private readonly IDisposable _hideInitiateSub;
    private readonly IDisposable _hideFailedSub;

    private Action<byte[]>? _wireSender;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<bool>? _isAutoHideEnabled;
    private Func<bool>? _isSneakBlockedByRoom;
    private Func<bool>? _isInParty;

    private StealthState _stateValue;
    private bool _sneakConfirmedThisRoom;
    private int _sneakRetries;

    // ----- followed -----------------------------------------------------------
    // A monster that follows us arrives a few milliseconds AFTER the new room's
    // display, so at the moment the arrival sn and the pre-step sn are decided the
    // room still looks empty — every step sent sn, drew "You may not sneak right now!",
    // and the cooldown hold stood us still under attack (backscroll 2026-10-04 14:50).
    // A monster arriving within FollowWindow of our room change marks us followed:
    // no sn and no stopping to cast while it lasts, since a sneak can't take with a
    // monster in the room (GAME_MECHANICS "Sneaking…"). It lifts when we leave a
    // room nobody followed us into — they've lost us, so the next room sneaks again.
    private static readonly TimeSpan FollowWindow = TimeSpan.FromMilliseconds(1500);
    private DateTimeOffset _roomChangedAt = DateTimeOffset.MinValue;
    private bool _followed;
    private bool _followedHere;

    public bool IsFollowed => _followed;

    // A monster arrived in our room (RoomEntryWatcher.ArrivalObserved).
    public void NoteMonsterArrival()
    {
        if (NowProvider() - _roomChangedAt > FollowWindow) return;
        _followedHere = true;
        if (_followed) return;
        _followed = true;
        _log?.Info(LogCategory,
            "followed — a monster came in right behind us; no sn until we leave a room nobody follows us into");
    }

    // A sneak can't take here: an NPC in the room, one following us that's about to
    // walk in behind us, or something in the pack that takes Stealth to nothing.
    private bool SneakBlockedHere() =>
        _followed || _isSneakBlockedByRoom?.Invoke() == true || _cantSneakCarrying?.Invoke() is not null;

    // ----- stood down for a carried item --------------------------------------
    // A few items cut Stealth just by being in the pack (a log raft, -125). With
    // one, every `sn` is refused, and Auto-Sneak used to resend it for the whole
    // settle window in each room (report paradigm-20261007-213809). Instead it
    // says so once on the terminal, sends nothing, and the walk goes on unsneaked
    // until the item is gone.
    private Func<string?>? _cantSneakCarrying;
    private Action<string>? _notice;
    private string? _stoodDownFor;

    // What Auto-Sneak has stood down for ("log raft (Stealth -125)"), or null.
    public string? StoodDownFor => _stoodDownFor;

    // cantSneakCarrying names the carried item(s) that make a sneak hopeless, or
    // returns null. notice writes a line to the terminal.
    public void SetCarriedPenaltyCheck(Func<string?> cantSneakCarrying, Action<string> notice)
    {
        ArgumentNullException.ThrowIfNull(cantSneakCarrying);
        ArgumentNullException.ThrowIfNull(notice);
        _cantSneakCarrying = cantSneakCarrying;
        _notice = notice;
    }

    // The pack or the Stealth figure changed: once nothing holds Stealth down any
    // more, say so. Standing down is announced when a sneak is next wanted.
    public void NoteCarriedChanged()
    {
        if (_stoodDownFor is not { } was || _cantSneakCarrying?.Invoke() is not null) return;
        _stoodDownFor = null;
        _log?.Info(LogCategory, $"auto-sneak back on — no longer carrying {was}");
        _notice?.Invoke("[Auto-Sneak: nothing in your pack holds your Stealth down any more - sneaking again]");
    }

    private bool StoodDownForCarried(string reason)
    {
        if (_cantSneakCarrying?.Invoke() is not { } what)
        {
            NoteCarriedChanged();
            return false;
        }
        if (_stoodDownFor != what)
        {
            _stoodDownFor = what;
            _notice?.Invoke($"[Auto-Sneak: you can't sneak while carrying {what} - going on unsneaked until it's gone]");
        }
        _log?.Info(LogCategory, $"auto-sneak suppressed ({reason}): carrying {what}");
        return true;
    }

    // ----- sneak-cooldown hold ---------------------------------------------
    private static readonly TimeSpan SneakCooldownRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SneakCooldownCap = TimeSpan.FromSeconds(15);
    private MovementCoordinator? _coordinator;
    private DateTimeOffset? _cooldownHoldSince;
    private Avalonia.Threading.DispatcherTimer? _cooldownTimer;

    public bool IsHoldingForSneakCooldown => _cooldownHoldSince is not null;

    // ----- sneak-settle hold -----------------------------------------------
    // An arrival sn holds movement until its answer lands (report
    // paradigm-20260927-003304: a flee resumed the walker, whose move went out ahead
    // of "You may not sneak right now!" and walked in seen). The pre-move sn isn't
    // held — the move is already going out behind it, and a sn that takes carries
    // into the next room.
    // Per answer: each soft-failed sn that's retried restarts it — a retry IS the
    // answer arriving (report paradigm-20260927-014032: the timer ran out on retry 2
    // of 10 and the loop stepped in seen). SneakSettleTotalCap bounds all the retries.
    private static readonly TimeSpan SneakSettleCap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SneakSettleTotalCap = TimeSpan.FromSeconds(15);
    private DateTimeOffset _settleSince;
    private bool _settleHold;
    private Avalonia.Threading.DispatcherTimer? _settleTimer;

    public bool IsHoldingForSneakAnswer => _settleHold;

    // Set when a sneak attempt ends without taking (retries spent, no answer, cooldown
    // cap) — the next step goes unsneaked rather than re-trying forever.
    private bool _moveUnsneakedOnce;

    // Asked by the walker / loop before a planned step goes out. Not sneaking and
    // able to: send sn, hold movement until the answer, and say "not yet" — the
    // engine resumes the step once the hold clears (report paradigm-20260927-013820:
    // the pre-move sn went out with the move and was refused, so we walked in seen).
    // Flee moves don't ask.
    public bool ReadyToMoveSneaking()
    {
        if (_isAutoSneakEnabled?.Invoke() != true || _coordinator is null) return true;
        if (_settleHold || _cooldownHoldSince is not null || _castHold) return false;
        if (CastWantsThisRoom())
        {
            BeginCastHold();
            return false;
        }
        // Hidden with a step planned: the move ends the hide, and sneaking is a flag of
        // its own that a hide doesn't set, so the step goes out sneaked only if an `sn`
        // takes first (GAME_MECHANICS "What ends a hide on Stock"). Where one can't
        // take, the step goes as it stands.
        if (_stateValue == StealthState.Hidden && !_state.InCombat && !SneakBlockedHere())
        {
            _log?.Info(LogCategory, "stepping out of a hide — the move ends it, so sneaking first");
            NoteHideBroken();
        }
        if (IsStealthed || _stateValue == StealthState.AttemptingSneak) return true;
        if (_moveUnsneakedOnce) { _moveUnsneakedOnce = false; return true; }
        if (_state.InCombat || SneakBlockedHere()) return true;
        if (_stateValue is not (StealthState.Idle or StealthState.Failed)) return true;
        TryBeginAutoSneak("before the step");
        return !_settleHold;
    }

    private void BeginSettleHold()
    {
        if (_coordinator is null || _settleHold) return;
        _settleHold = true;
        _settleSince = NowProvider();
        _coordinator.AssertGate(MovementCoordinator.SneakSettleGate, nameof(StealthManager), "waiting for the sn answer");
        StartSettleTimer();
    }

    private void StartSettleTimer()
    {
        _settleTimer?.Stop();
        _settleTimer = new Avalonia.Threading.DispatcherTimer(SneakSettleCap,
            Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
            {
                _moveUnsneakedOnce = true;
                ReleaseSettleHold("no answer in time");
            });
        _settleTimer.Start();
    }

    // Reset States: release the sn-answer, combat-cooldown and held-cast movement holds.
    public void ReleaseMovementHolds(string why)
    {
        ReleaseSettleHold(why);
        ReleaseCooldownHold(why);
        ReleaseCastHold(why);
    }

    // A buff / heal / cure that sneak keeping held while we moved (the sneaked step is
    // always landing, so it never finds a gap) goes out in the first NPC-free room:
    // the step waits for it, the arrival `sn` waits too, and the re-sneak after the
    // cast (ReSneakAfterCast) sends us on (report paradigm-20260928-165844). Capped:
    // a cast that doesn't go out in time is given up for this room.
    private static readonly TimeSpan CastHoldCap = TimeSpan.FromSeconds(7);
    private Func<bool>? _castDue;
    private bool _castHold;
    private bool _castHoldSpentThisRoom;
    private DateTimeOffset _castHoldSince;
    private Avalonia.Threading.DispatcherTimer? _castHoldTimer;

    public void SetHeldCastCheck(Func<bool> castDue) => _castDue = castDue;

    public bool IsHoldingForCast => _castHold;

    private bool CastWantsThisRoom() =>
        !_castHoldSpentThisRoom
        && !_state.InCombat
        && !SneakBlockedHere()
        && _castDue?.Invoke() == true;

    private void BeginCastHold()
    {
        if (_coordinator is null || _castHold) return;
        _castHold = true;
        _castHoldSince = NowProvider();
        _coordinator.AssertGate(MovementCoordinator.SneakCastGate, nameof(StealthManager),
            "a held buff goes out in this NPC-free room before we re-sneak");
        _castHoldTimer?.Stop();
        _castHoldTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1),
            Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
            {
                if (_castDue?.Invoke() != true)
                    ReleaseCastHold("nothing left to cast here");
                else if (NowProvider() - _castHoldSince >= CastHoldCap)
                {
                    _castHoldSpentThisRoom = true;
                    ReleaseCastHold($"the cast didn't go out in {CastHoldCap.TotalSeconds:0}s — moving on");
                }
            });
        _castHoldTimer.Start();
    }

    private void ReleaseCastHold(string why)
    {
        _castHoldTimer?.Stop();
        _castHoldTimer = null;
        if (!_castHold) return;
        _castHold = false;
        _coordinator?.ClearGate(MovementCoordinator.SneakCastGate, nameof(StealthManager), why);
    }

    private void ReleaseSettleHold(string why)
    {
        _settleTimer?.Stop();
        _settleTimer = null;
        if (!_settleHold) return;
        _settleHold = false;
        _coordinator?.ClearGate(MovementCoordinator.SneakSettleGate, nameof(StealthManager), why);
    }
    private bool _disposed;

    // Current FSM state. Backed by PlayerState.IsSneaking / PlayerState.IsHidden
    // for observables; the FSM state itself is exposed via this property + the
    // StateChanged event.
    public StealthState State => _stateValue;

    // Fires after every confirmed FSM transition (including silent-loss to Idle).
    // Args: old state, new state.
    public event Action<StealthState, StealthState>? StateChanged;

    // Fires when silent-loss is detected on room change — we believed we were
    // sneaking but the new room's emit didn't carry the "Sneaking..."
    // confirmation.
    public event Action? SilentSneakLost;

    // Fires once per room entered while sneaking: true when the room confirmed it
    // (`Sneaking...`), false when it broke on the way in — the loud line or a silent
    // loss. Feeds the session's sneak success rate.
    public event Action<bool>? SneakEntry;

    public StealthManager(
        MessageRouter router,
        PlayerState state,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _log = log;

        _sneakingSub      = router.Subscribe(KnownPatterns.UserSneaking,      OnSneaking);
        _notSneakingSub   = router.Subscribe(KnownPatterns.UserNotSneaking,   OnNotSneaking);
        _sneakInitiateSub = router.Subscribe(KnownPatterns.UserSneakInitiate, OnSneakInitiate);
        _sneakFailedSub   = router.Subscribe(KnownPatterns.UserSneakFailed,   OnSneakFailed);
        _cantSneakSub     = router.Subscribe(KnownPatterns.UserCantSneak,     OnCantSneak);
        _hideInitiateSub  = router.Subscribe(KnownPatterns.UserHideInitiate,  OnHideInitiate);
        _hideFailedSub    = router.Subscribe(KnownPatterns.UserHideFailed,    OnHideFailed);
    }

    // Bind the wire sender for the auto-sneak / auto-hide engines. Until set, the
    // auto-engines log decisions but don't send commands.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the AutoSneak / AutoHide master switches — typically
    // GeneralSettings.AutoMode.AutoSneak / AutoHide. When the funcs are null the
    // auto-engines stay dormant (passive tracker behavior).
    public void SetAutoToggles(Func<bool> isAutoSneakEnabled, Func<bool> isAutoHideEnabled)
    {
        _isAutoSneakEnabled = isAutoSneakEnabled;
        _isAutoHideEnabled = isAutoHideEnabled;
    }

    // Wire the "sneak blocked by a room occupant" predicate — true when any NPC is
    // in the current room (any NPC at all prevents sneak in MajorMUD). AppServices
    // binds this to CombatStateTracker.HasRoomNpc. When the predicate returns
    // true, auto-sneak suppresses the doomed sn rather than firing it into a
    // guaranteed server rejection. When null, no suppression occurs (sneak is
    // attempted unconditionally).
    public void SetSneakBlockCheck(Func<bool> isSneakBlockedByRoom)
    {
        ArgumentNullException.ThrowIfNull(isSneakBlockedByRoom);
        _isSneakBlockedByRoom = isSneakBlockedByRoom;
    }

    // Wire the "currently in a party" predicate. Auto-hide is suppressed while in a
    // party: a hidden member drops off the room's Also-here line and can no longer
    // be single-target-healed or buffed by other members until revealed (only
    // room-wide / party-wide spells reach them). Manual hide the user types is NOT
    // gated — only the automatic idle-hide. When null, no party suppression occurs.
    public void SetPartyCheck(Func<bool> isInParty)
    {
        ArgumentNullException.ThrowIfNull(isInParty);
        _isInParty = isInParty;
    }

    // True while we hold any active stealth (Sneaking or Hidden). Read by
    // CombatManager's backstab gate to open with bs instead of a normal attack.
    // Casting DOES break both Sneak and Hide (GAME_MECHANICS.md), but that's an
    // accepted cost the automation pays to keep buffs up rather than a reason to
    // suppress the cast — CastingDirector does not gate on this.
    public bool IsStealthed =>
        _stateValue == StealthState.Sneaking || _stateValue == StealthState.Hidden;

    // A sneaked move is out and the new room's "Sneaking..." hasn't come yet. It
    // arrives between the move and the room display, so a display without it means
    // the sneak silently broke (GAME_MECHANICS "Sneaking…"; report
    // paradigm-20260927-014325: `sn` was acknowledged, we walked in, no "Sneaking...",
    // and the backstab still went out). The old room's `sn` acknowledgement doesn't
    // count for the new one.
    private bool _awaitingArrivalConfirm;

    // Stealthed in THIS room: hidden, or sneaking with the arrival confirmed. The
    // backstab opener reads this, so a silent break never opens with `bs`.
    // A refused move never left the room, so there's no new room to confirm.
    public void NoteMoveBlocked() => _awaitingArrivalConfirm = false;

    public bool IsStealthedHere =>
        _stateValue == StealthState.Hidden
        || (_stateValue == StealthState.Sneaking && !_awaitingArrivalConfirm);

    // True only while actively StealthState.Sneaking (not Hidden). Kept distinct
    // from IsStealthed because sneak carries a per-room re-confirm + silent-loss
    // watchdog that hide doesn't. The backstab opener gates on IsStealthed (either
    // state opens), so consumers wanting "sneaking specifically" use this.
    public bool IsSneaking => _stateValue == StealthState.Sneaking;

    // Called by an external observer (RoomTracker via AppServices) when the
    // player's room changes. If we believed we were sneaking but didn't observe
    // the "Sneaking..." line in the new room within a short window after the room
    // display, we treat it as a silent loss. Practically: callers invoke this
    // AFTER the new room's emit batch has been processed by the router
    // (RoomTracker's StateChanged fires after the room display lands), so the
    // "Sneaking..." emit (if any) has already updated _sneakConfirmedThisRoom.
    public void NoteRoomChanged()
    {
        // Nobody came in behind us in the room we just left: they've lost us.
        if (_followed && !_followedHere)
        {
            _followed = false;
            _log?.Info(LogCategory, "no longer followed — nothing came in behind us last room; sneaking again");
        }
        _followedHere = false;
        _roomChangedAt = NowProvider();

        // We moved anyway (a manual step, a flee) — the hold was for the room we left.
        ReleaseCooldownHold("moved");
        ReleaseSettleHold("moved");
        ReleaseCastHold("moved");
        _castHoldSpentThisRoom = false;

        // Moving breaks hide — you can't move while hidden, so a confirmed room
        // change means any optimistic hidden state is gone. Cleared before the
        // auto-sneak re-attempt below so a hand-move out of a hidden room can
        // re-establish sneak for the next leg.
        if (_stateValue == StealthState.Hidden)
            NoteHideBroken();

        if (_stateValue == StealthState.Sneaking && (!_sneakConfirmedThisRoom || _awaitingArrivalConfirm))
        {
            _log?.Info(LogCategory, "silent sneak loss — new room without 'Sneaking...' confirm");
            Transition(StealthState.Idle);
            _state.IsSneaking = false;
            SilentSneakLost?.Invoke();
            SneakEntry?.Invoke(false);
        }
        else if (_stateValue == StealthState.AttemptingSneak && !_sneakConfirmedThisRoom)
        {
            // An in-flight sneak attempt whose ACK ("Attempting to sneak...") and
            // room "Sneaking..." confirm both went unobserved before the room
            // changed. The attempt is now stale — reset to Idle so the re-attempt
            // below isn't blocked by TryBeginAutoSneak's in-flight guard
            // (!= Idle && != Failed). Without this the FSM strands in
            // AttemptingSneak and auto-sneak silently stops re-firing for the
            // rest of the run. Not a SilentSneakLost — we never established sneak,
            // so there's no armed state for consumers to unwind.
            _log?.Info(LogCategory, "stale sneak attempt — room changed before confirm; resetting to re-attempt");
            Transition(StealthState.Idle);
        }
        _sneakConfirmedThisRoom = false;
        _awaitingArrivalConfirm = false;
        _staleSneakAcks = 0;
        _staleHideAcks = 0;

        // Auto-sneak: fires after the silent-loss check so a just-lost
        // sneak immediately re-attempts. This is the reactive path —
        // covers room changes from manual movement the user types at the
        // terminal. Engine-driven moves get the proactive pre-move path
        // (RequestPreMoveStealth) instead, so the move itself is sneaked
        // rather than the room after it.
        TryBeginAutoSneak("room change + idle/failed + !combat");
    }

    // Called by CombatStateTracker when a room clears of engageable hostiles and
    // combat ends. Attacking spends stealth — the surprise opener is one-shot and
    // engaging reveals you (see GAME_MECHANICS) — but there's no line-driven signal
    // for it, so a sneak/hide we held going into the fight is now stale-true. Drop
    // it to Idle so the pre-move auto-sneak (RequestPreMoveStealth, fired as the
    // walker steps out of the cleared room) sees a settled non-stealth state and
    // re-establishes sneak for the move instead of no-opping on the stale state
    // (report stock-20260730-163044). No `sn` is sent here — the re-attempt is the
    // pre-move hook's job.
    public void NoteCombatEndedStealthReset(bool roomClearedByKill = false)
    {
        // A fight that ran its course here means whatever followed us is dealt with.
        // Right after a move it's only the room display reading clear, with the
        // followers still about to walk in, so keep the flag then — unless a kill
        // emptied the room: a backstab can drop the follower inside the window, and
        // keeping the flag sent the next step out unsneaked (report
        // paradigm-20261004-180252).
        if (_followed && (roomClearedByKill || NowProvider() - _roomChangedAt > FollowWindow))
        {
            _followed = false;
            _log?.Info(LogCategory, "no longer followed — the fight here is over");
        }
        if (_stateValue == StealthState.Sneaking)
        {
            _log?.Info(LogCategory, "combat spent sneak — resetting for pre-move re-sneak");
            Transition(StealthState.Idle);
            _state.IsSneaking = false;
            // The room-clear counts as this room's "confirm"; without clearing it a
            // later NoteRoomChanged could mis-read a genuine silent loss.
            _sneakConfirmedThisRoom = false;
        }
        else if (_stateValue == StealthState.Hidden)
        {
            _log?.Info(LogCategory, "combat spent hide — resetting stealth");
            NoteHideBroken();
        }
        // Whatever we went into the fight with, the room is clear now: with nothing
        // driving the moves, take the sneak back here.
        ScheduleInPlaceReSneak();
    }

    // ----- Re-sneak in place (nothing driving the moves) ----------------
    // A walk, loop or auto-lair re-takes a broken sneak at its pre-move hook, the
    // right moment for it. With none running — the player walking by hand — there is
    // no such moment: the only other trigger is arriving in a room with no NPC, and in
    // a tunnel with a monster in every room that never comes, so a sneak broken by a
    // typed `sea` or spent on a fight stayed broken and every later room was entered
    // seen (report paradigm-20261002-004148). So with nothing driving, a broken sneak
    // is re-taken where we stand, a beat later: a gear swap is a burst of commands,
    // and an `sn` sent into the middle of one would be broken again by the next.
    private static readonly TimeSpan InPlaceReSneakDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan InPlaceRestRecheck = TimeSpan.FromSeconds(2);
    private Func<bool>? _isEngineDriving;
    private Func<bool>? _restUnderWay;
    private Func<bool>? _restKeepsSneak;
    private bool _waitingOnRestLogged;
    private Avalonia.Threading.DispatcherTimer? _inPlaceTimer;

    // True while a walk, loop or auto-lair is driving the moves. Unwired, nothing is
    // ever re-taken in place.
    public void SetEngineDrivingCheck(Func<bool> isEngineDriving)
    {
        ArgumentNullException.ThrowIfNull(isEngineDriving);
        _isEngineDriving = isEngineDriving;
    }

    // How a rest and a sneak share the spot we stand on (GAME_MECHANICS "Sneaking and
    // resting in place"). restUnderWay: resting or meditating, by the engine or by
    // hand, with a pool still short of rest-max. restKeepsSneak: a ShadowRest
    // character, whose rest an `sn` neither breaks nor is ended by.
    public void SetIdleRestChecks(Func<bool> restUnderWay, Func<bool> restKeepsSneak)
    {
        ArgumentNullException.ThrowIfNull(restUnderWay);
        ArgumentNullException.ThrowIfNull(restKeepsSneak);
        _restUnderWay = restUnderWay;
        _restKeepsSneak = restKeepsSneak;
    }

    // Auto-Sneak was just switched on: sneak now rather than at the next clear room.
    public void NoteAutoSneakSwitchedOn() => ScheduleInPlaceReSneak();

    // The player typed a move. With nothing driving the moves, this is the pre-move
    // moment an engine has: send the `sn` ahead of the step so the step itself is
    // sneaked, however soon after a sneak-ending command it was typed.
    public void NoteTypedMove()
    {
        if (_isEngineDriving?.Invoke() != false) return;
        RequestPreMoveStealth();
    }

    // ----- what ends a hide ---------------------------------------------------
    // The engine's list of what ends a hide (GAME_MECHANICS "Hiding — sneak vs hide,
    // the hide state machine, and search reveals" → "What ends a hide on Stock") was
    // read from Stock and is applied on both realms, as the sneak list is.

    // Answers still owed to an `hid` that a later hide-ending command has undone, the
    // counterpart of _staleSneakAcks. A room change clears it.
    private int _staleHideAcks;

    // Drop a hide that something just sent has ended. True when there was one to drop.
    private bool DropHide(string what)
    {
        if (_stateValue == StealthState.Hidden)
        {
            _log?.Info(LogCategory, $"{what} ended the hide");
            NoteHideBroken();
            return true;
        }
        if (_stateValue != StealthState.AttemptingHide) return false;
        // The `hid` went out BEFORE this, so the game runs the hide and then ends it.
        // Its "Attempting to hide..." is still on its way and must not read as hidden.
        _log?.Info(LogCategory, $"{what} ended the hide still being attempted");
        _staleHideAcks++;
        Transition(StealthState.Idle);
        _state.IsHidden = false;
        return true;
    }

    // A direction went out. The game's move command zeroes the hidden state as it is
    // taken, before it knows whether the move goes anywhere, so a move refused at a
    // wall ends a hide too and no room change would say so. Runs ahead of
    // NoteTypedMove, so a step typed out of a hide is sneaked like any other.
    public void NoteDirectionalMoveSent() => DropHide("a move");

    private void ScheduleInPlaceReSneak()
    {
        if (_isEngineDriving?.Invoke() != false) return;
        if (_isAutoSneakEnabled?.Invoke() != true) return;
        ArmInPlaceTimer(InPlaceReSneakDelay);
    }

    private void ArmInPlaceTimer(TimeSpan delay)
    {
        _inPlaceTimer?.Stop();
        _inPlaceTimer = new Avalonia.Threading.DispatcherTimer(delay,
            Avalonia.Threading.DispatcherPriority.Background, (_, _) => ReSneakInPlace());
        _inPlaceTimer.Start();
    }

    internal void ReSneakInPlaceForTests() => ReSneakInPlace();

    internal bool InPlaceReSneakPendingForTests => _inPlaceTimer is not null;

    private void ReSneakInPlace()
    {
        _inPlaceTimer?.Stop();
        _inPlaceTimer = null;
        // An engine started in the meantime owns it from here.
        if (_isEngineDriving?.Invoke() != false || _isAutoSneakEnabled?.Invoke() != true)
        {
            _waitingOnRestLogged = false;
            return;
        }
        // `sn` stands a resting character up and `rest` ends a sneak, so a rest still
        // short of rest-max is left to finish — one the player typed as much as the
        // engine's own — and this looks again. Once it has topped off, the sneak goes
        // out. On Paradigm an `sn` doesn't break a rest, and a ShadowRest rest keeps
        // the sneak, so a ShadowRest character sneaks right where it rests.
        bool restWins = _skipReSneakForRest?.Invoke() == true
            || (_restUnderWay?.Invoke() == true && _restKeepsSneak?.Invoke() != true);
        if (restWins)
        {
            if (!_waitingOnRestLogged)
                _log?.Info(LogCategory, "re-sneak in place waits — a sneak would break the rest; sneaking once it reaches rest-max");
            _waitingOnRestLogged = true;
            ArmInPlaceTimer(InPlaceRestRecheck);
            return;
        }
        _waitingOnRestLogged = false;
        TryBeginAutoSneak("in place — nothing is driving the moves");
    }

    // The client sent a command that ends a sneak (GAME_MECHANICS "What ends a sneak"):
    // a gear swap, a search, a door or trap step, a say. The server prints nothing we
    // could latch, so without this the FSM reads stale-Sneaking and the next move goes
    // out unsneaked. Drop to Idle; the next move's ready check re-sneaks. Not in place:
    // a gear swap is a burst of commands, and an `sn` mid-burst would be broken again.
    //
    // endsHide: the command ends a hide as well ("What ends a hide on Stock"), just as
    // silently. There is no re-hide for any break, so the hide is only dropped, and
    // Auto-Sneak takes over as it does after a cast.
    public void NoteSneakBroken(string what, bool endsHide = false)
    {
        if (endsHide && DropHide(what))
        {
            ScheduleInPlaceReSneak();
            return;
        }
        if (_stateValue is not (StealthState.Sneaking or StealthState.AttemptingSneak)) return;
        _log?.Info(LogCategory, $"{what} ended the sneak — re-sneaking before the next move");
        // An `sn` still unanswered was sent BEFORE this command, so the game takes the
        // sneak and then ends it. Its "Attempting to sneak..." is on its way and must
        // not be read as sneaking (report paradigm-20261002-004148: an arrival `sn`
        // followed by `sea` left the client believing it sneaked into the next room).
        if (_stateValue == StealthState.AttemptingSneak) _staleSneakAcks++;
        Transition(StealthState.Idle);
        _state.IsSneaking = false;
        _sneakConfirmedThisRoom = false;
        _awaitingArrivalConfirm = false;
        ScheduleInPlaceReSneak();
    }

    // Called after an automated out-of-combat cast fires (CastFired). A cast breaks
    // both Sneak and Hide (GAME_MECHANICS) but the server emits no line we can latch,
    // so the FSM would otherwise read stale-Sneaking and the next auto-sneak attempt
    // (reactive or pre-move) would no-op on it. Drop the spent stealth to Idle, then
    // re-attempt sneak IN PLACE so a character standing in a cleared room re-sneaks
    // without waiting for its next move. No-op unless auto-sneak is on and we're out
    // of combat — an in-combat cast never re-sneaks here (NoteCombatEndedStealthReset
    // owns the combat-end reset), and TryBeginAutoSneak's own gates (settled state,
    // no NPC present) decide whether the `sn` actually goes out.
    //
    // The cast hold comes off LAST: clearing it can resume the walker on the spot, and
    // it must find stealth already reset and the re-sneak's settle hold up — released
    // first, the next move went out unsneaked, ahead of the `sn` (reports
    // paradigm-20260929-185544, paradigm-20260929-191106).
    public void ReSneakAfterCast()
    {
        // Drop the hold's flag now (a held cast would stop the re-sneak from starting)
        // but keep its movement gate until the re-sneak's own is up.
        bool gateUp = _castHold;
        _castHoldTimer?.Stop();
        _castHoldTimer = null;
        _castHold = false;
        try
        {
            // A cast ends a hide whether or not there is a sneak to take back after
            // it, and an item's cast drops one on its `use` (NoteSneakBroken). So the
            // two agree, a spell cast drops it ahead of the gates below as well.
            DropHide("a cast");
            if (_isAutoSneakEnabled?.Invoke() != true) return;
            if (_state.InCombat) return;
            if (_stateValue == StealthState.Sneaking)
            {
                _log?.Info(LogCategory, "cast spent sneak — resetting for re-sneak");
                Transition(StealthState.Idle);
                _state.IsSneaking = false;
                _sneakConfirmedThisRoom = false;
            }
            else if (_stateValue == StealthState.Hidden)
            {
                _log?.Info(LogCategory, "cast spent hide — resetting stealth");
                NoteHideBroken();
            }
            if (_skipReSneakForRest?.Invoke() == true)
            {
                _log?.Info(LogCategory, "cast during a rest — not re-sneaking; the rest would end it, the next move re-sneaks");
                return;
            }
            // An item cast also marked the sneak broken on its `use`, which arms the
            // in-place re-sneak for a moment later. This `sn` answers both.
            if (TryBeginAutoSneak("post-cast re-sneak"))
            {
                _inPlaceTimer?.Stop();
                _inPlaceTimer = null;
            }
        }
        finally
        {
            if (gateUp)
                _coordinator?.ClearGate(MovementCoordinator.SneakCastGate, nameof(StealthManager), "the held cast went out");
        }
    }

    // Movement-engine pre-move hook — called by the walker / loop runner
    // immediately before a move's bytes go out (after any door / trap / hidden /
    // multi-action pre-steps) so the move itself is performed under sneak.
    // Non-blocking: fires sn and returns at once; the move is NOT held waiting for
    // the ACK (sneak carries through the move and the new room's "Sneaking..."
    // line confirms it). No-op when auto-sneak is off, we're already sneaking /
    // hidden or mid-attempt, or we're in combat (the walker is gated out of moving
    // while a hostile holds the Combat gate anyway).
    public void RequestPreMoveStealth()
    {
        TryBeginAutoSneak("pre-move");
        // Whatever we had, the room we're stepping into has to confirm it.
        if (_stateValue is StealthState.Sneaking or StealthState.AttemptingSneak)
            _awaitingArrivalConfirm = true;
    }

    // Shared auto-sneak entry point. Sends sn exactly once from a settled
    // non-stealth state (StealthState.Idle / StealthState.Failed) when auto-sneak
    // is on and we're not in combat, transitioning to
    // StealthState.AttemptingSneak. No-op when auto-sneak is off, a sneak / hide
    // is already established or in flight, or we're in combat. The settled-state
    // guard prevents a double-send when the reactive room-change path and the
    // pre-move path both fire for the same move.
    // A ShadowRest character (Paradigm) sneaks before it rests, so the rest keeps the
    // stealth (user, 2026-09-28: "send sneak then rest"). True while that sneak is
    // still settling — hold the rest a moment; false to rest now: already stealthed,
    // in combat, an NPC here (a sneak won't take), or two tries already failed. Not
    // tied to the auto-sneak toggle: the user opted into ShadowRest itself.
    public bool SneakBeforeRest()
    {
        if (IsStealthed) { _restSneakTries = 0; return false; }
        if (_stateValue == StealthState.AttemptingSneak) return true;
        if (_state.InCombat || SneakBlockedHere()) return false;
        if (_stateValue is not (StealthState.Idle or StealthState.Failed)) return false;
        if (_restSneakTries >= 2) return false;
        _restSneakTries++;
        _log?.Info(LogCategory, "sneaking before the rest (ShadowRest)");
        _sneakRetries = 0;
        Transition(StealthState.AttemptingSneak);
        Send("sn");
        return true;
    }

    private int _restSneakTries;

    // True while a rest is under way that would end a sneak — any rest, unless the
    // user utilizes ShadowRest on a race or class that has it. A cast mid-rest then
    // leaves the sneak off instead of re-sneaking only for the next `rest` to end it
    // (report paradigm-20260930-184343).
    private Func<bool>? _skipReSneakForRest;

    public void SetReSneakSkipForRest(Func<bool> skip) => _skipReSneakForRest = skip;

    // While true (fleeing with the emergency heal due), the auto re-sneak waits: the
    // heal ends a sneak, so it goes first and the re-sneak follows it (CastFired →
    // ReSneakAfterCast). It never holds a flee step — the step just goes unsneaked.
    private Func<bool>? _holdSneakForHeal;
    private bool _heldForHealLogged;

    public void SetSneakHoldForHeal(Func<bool> hold) => _holdSneakForHeal = hold;

    private bool TryBeginAutoSneak(string reason)
    {
        if (_isAutoSneakEnabled?.Invoke() != true) return false;
        if (_state.InCombat) return false;
        if (_holdSneakForHeal?.Invoke() == true)
        {
            if (!_heldForHealLogged)
                _log?.Info(LogCategory, $"auto-sneak waits ({reason}): the emergency heal goes first");
            _heldForHealLogged = true;
            return false;
        }
        _heldForHealLogged = false;
        if (_stateValue != StealthState.Idle && _stateValue != StealthState.Failed) return false;
        // A held buff is about to go out here, and it would end the sneak straight
        // away — the re-sneak after it does the job.
        if (reason != "pre-move" && (_castHold || CastWantsThisRoom()))
        {
            _log?.Info(LogCategory, $"auto-sneak waits ({reason}): a held buff goes out first");
            return false;
        }

        if (StoodDownForCarried(reason)) return false;

        // Any NPC in the room prevents sneak from taking — don't burn an
        // `sn` the server will reject. The move (if engine-driven)
        // proceeds regardless; sneak re-attempts once the room is clear.
        if (_followed)
        {
            _log?.Info(LogCategory, $"auto-sneak suppressed ({reason}): being followed");
            return false;
        }
        if (_isSneakBlockedByRoom?.Invoke() == true)
        {
            _log?.Info(LogCategory, $"auto-sneak suppressed ({reason}): NPC present");
            return false;
        }

        // `sn` is the live MajorMUD command; the clean
        // `Attempting to sneak...` ACK then establishes the armed sneak
        // (OnSneakInitiate) and the move's `Sneaking...` confirms it.
        _log?.Info(LogCategory, $"auto-sneak triggered ({reason})");
        _sneakRetries = 0;
        Transition(StealthState.AttemptingSneak);
        if (reason != "pre-move") BeginSettleHold();
        Send("sn");
        return true;
    }

    // Called when the player goes idle (e.g. enters a safe room and the walker
    // stops). Drives the auto-hide engine. Wire from AppServices on whatever
    // "idle" signal is appropriate — for v1, fire it from the same
    // RoomTracker.StateChanged hook as auto-sneak; hide is silent on failure and
    // the server rate-limits attempts.
    public void NoteIdleOpportunity()
    {
        if (_isAutoHideEnabled?.Invoke() != true) return;
        if (_state.InCombat) return;
        // Already hidden, or a hid is in flight waiting on the ambiguous
        // "Attempting to hide..." line — don't stack another attempt.
        if (_stateValue == StealthState.Hidden || _stateValue == StealthState.AttemptingHide) return;
        // Never auto-hide in a party: a hidden member is removed from the room's
        // Also-here line and can't be single-target-healed/buffed by the party
        // until revealed. Manual hide is the user's call; auto-hide must not.
        if (_isInParty?.Invoke() == true)
        {
            _log?.Info(LogCategory, "auto-hide suppressed: in a party");
            return;
        }
        _log?.Info(LogCategory, "auto-hide triggered");
        Transition(StealthState.AttemptingHide);
        Send("hid");
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(System.Text.Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Optimistically establish hidden — driven by the bare "Attempting to hide..."
    // line (OnHideInitiate). Hide success is NOT self-observable: the server runs a
    // check but never tells us it landed, so we assume hidden and let the backstab
    // surprise-round resolver confirm or deny after the opener swings. The
    // AttemptingHide -> Hidden transition is what re-arms the backstab opener (via
    // StateChanged), so a fresh hide re-opens the surprise round for a monster that
    // walks into the room.
    public void NoteHideConfirmed()
    {
        Transition(StealthState.Hidden);
        _state.IsHidden = true;
        // Mutually exclusive with sneaking — clear the sneak flag so both
        // observables never read true at once (hiding after a sneak-approach).
        _state.IsSneaking = false;
    }

    // Clear hide — the explicit failure line, a move (NoteRoomChanged), or any
    // other hide-breaking event.
    public void NoteHideBroken()
    {
        if (_stateValue == StealthState.Hidden)
            Transition(StealthState.Idle);
        _state.IsHidden = false;
    }

    // ----- handlers ----------------------------------------------------

    // Answers still owed to an `sn` that a later sneak-ending command has undone
    // (NoteSneakBroken). A room change clears it: those lines aren't coming.
    private int _staleSneakAcks;

    private void OnSneakInitiate(MatchResult _)
    {
        if (_staleSneakAcks > 0)
        {
            _staleSneakAcks--;
            _log?.Info(LogCategory, "sneak answer ignored — a later command already ended that sneak");
            // The step was held for this answer; let the next ready check re-sneak.
            ReleaseSettleHold("that sneak was ended by a later command");
            return;
        }
        // Clean `Attempting to sneak...` (no failure suffix — the
        // anchored UserSneakInitiate pattern guarantees that) is the
        // server ACK: the sneak took and we're armed to move.
        EstablishSneaking();
    }

    private void OnSneaking(MatchResult _)
    {
        // `Sneaking...` on room entry — post-move confirmation we
        // arrived unseen. Re-arms the silent-loss watchdog.
        _awaitingArrivalConfirm = false;
        EstablishSneaking();
        SneakEntry?.Invoke(true);
    }

    // Shared positive-signal handler: marks sneak established (clean initiate ACK
    // or post-move Sneaking...), arms the per-room watchdog, and clears the resend
    // counter.
    private void EstablishSneaking()
    {
        _moveUnsneakedOnce = false;
        // A sneak that's since taken supersedes an earlier loud entry — that break
        // belonged to a room we've left (report paradigm-20260927-011624).
        _sneakBrokeOnEntryAt = DateTimeOffset.MinValue;
        ReleaseSettleHold("sneaking");
        ReleaseCooldownHold("sneaking");
        _sneakConfirmedThisRoom = true;
        _sneakRetries = 0;
        if (_stateValue == StealthState.Sneaking) return;
        Transition(StealthState.Sneaking);
        _state.IsSneaking = true;
        // Sneaking and hidden are mutually exclusive — establishing one clears the
        // other so the two observables never read true at once (e.g. hiding, then
        // a sneak re-establishes on the next approach).
        _state.IsHidden = false;
    }

    private void OnNotSneaking(MatchResult _)
    {
        if (_stateValue == StealthState.Idle && !_state.IsSneaking) return;
        _sneakBrokeOnEntryAt = NowProvider();
        Transition(StealthState.Idle);
        _state.IsSneaking = false;
        SneakEntry?.Invoke(false);
    }

    // "You make a sound as you enter the room!" lands just before the room display,
    // and it means the backstab we snuck in for would fail. Combat asks once, while
    // it decides the room's opener; the window keeps a stale break from a quiet room
    // from applying to a later, unrelated one.
    private static readonly TimeSpan SneakBrokeWindow = TimeSpan.FromSeconds(3);
    private DateTimeOffset _sneakBrokeOnEntryAt = DateTimeOffset.MinValue;

    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    // True (once) when our sneak broke entering the current room.
    public bool TakeSneakBrokeOnEntry()
    {
        bool broke = NowProvider() - _sneakBrokeOnEntryAt <= SneakBrokeWindow;
        _sneakBrokeOnEntryAt = DateTimeOffset.MinValue;
        // A sneaked move whose room showed up without "Sneaking..." broke silently —
        // the same guaranteed backstab failure as a loud entry.
        if (!broke && _awaitingArrivalConfirm
            && _stateValue is StealthState.Sneaking or StealthState.AttemptingSneak)
        {
            _log?.Info(LogCategory, "silent sneak loss — the room showed without 'Sneaking...'");
            broke = true;
        }
        return broke;
    }

    private void OnSneakFailed(MatchResult _)
    {
        if (_staleSneakAcks > 0) _staleSneakAcks--;
        // `Attempting to sneak...You don't think you're sneaking.` — the
        // attempt was rejected. Resend `sn` when auto-sneak owns the loop
        // (capped at MaxSneakRetries); otherwise settle on Failed and let
        // the caller decide whether to retry or move without sneaking.
        Transition(StealthState.Failed);
        if (_isAutoSneakEnabled?.Invoke() == true
         && !_state.InCombat
         && (_sneakRetries < MaxSneakRetries || _settleHold))
        {
            if (_settleHold && NowProvider() - _settleSince >= SneakSettleTotalCap)
            {
                _moveUnsneakedOnce = true;
                ReleaseSettleHold($"still not sneaking after {SneakSettleTotalCap.TotalSeconds:0}s — moving on unsneaked");
                return;
            }
            _sneakRetries++;
            _log?.Info(LogCategory, $"sneak rejected — resending sn (retry {_sneakRetries}/{MaxSneakRetries})");
            Transition(StealthState.AttemptingSneak);
            if (_settleHold) StartSettleTimer();
            Send("sn");
            return;
        }
        _moveUnsneakedOnce = true;
        ReleaseSettleHold("sneak rejected, retries spent");
    }

    private void OnCantSneak(MatchResult _)
    {
        if (_staleSneakAcks > 0) _staleSneakAcks--;
        ReleaseSettleHold("cooldown");   // the cooldown hold below takes over
        Transition(StealthState.Failed);
        if (_isAutoSneakEnabled?.Invoke() != true || _coordinator is null) return;
        // Refused because a monster is here (it followed us in): retrying can't work
        // until we're away from it, and holding the walk only lets it keep hitting us.
        if (SneakBlockedHere())
        {
            _log?.Info(LogCategory, "sneak refused with a monster here — moving on unsneaked, no hold");
            return;
        }
        if (_cooldownHoldSince is null)
        {
            _cooldownHoldSince = NowProvider();
            _coordinator.AssertGate(MovementCoordinator.SneakCooldownGate, nameof(StealthManager),
                "sneak on cooldown — holding the move and retrying sn");
            _log?.Info(LogCategory, "sneak refused (cooldown) — holding movement, retrying sn");
        }
        if (_cooldownTimer is null)
        {
            _cooldownTimer = new Avalonia.Threading.DispatcherTimer(SneakCooldownRetry,
                Avalonia.Threading.DispatcherPriority.Background, (_, _) => RetrySneakAfterCooldown());
            _cooldownTimer.Start();
        }
    }

    // Bind the movement coordinator so a sneak cooldown can hold the route.
    public void SetMovementCoordinator(MovementCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    internal void RetrySneakAfterCooldownForTests() => RetrySneakAfterCooldown();

    private void RetrySneakAfterCooldown()
    {
        if (_cooldownHoldSince is not { } since) { StopCooldownTimer(); return; }
        if (_isAutoSneakEnabled?.Invoke() != true)
        {
            ReleaseCooldownHold("auto-sneak turned off");
            return;
        }
        // A fight or an NPC here makes sneaking impossible anyway — combat's own gate
        // takes over.
        if (_state.InCombat || SneakBlockedHere())
        {
            ReleaseCooldownHold("combat / NPC in the room");
            return;
        }
        if (NowProvider() - since >= SneakCooldownCap)
        {
            _moveUnsneakedOnce = true;
            ReleaseCooldownHold($"still refused after {SneakCooldownCap.TotalSeconds:0}s — moving on unsneaked");
            return;
        }
        if (_stateValue is StealthState.Idle or StealthState.Failed)
            TryBeginAutoSneak("cooldown retry");
    }

    private void ReleaseCooldownHold(string why)
    {
        StopCooldownTimer();
        if (_cooldownHoldSince is null) return;
        _cooldownHoldSince = null;
        _coordinator?.ClearGate(MovementCoordinator.SneakCooldownGate, nameof(StealthManager), why);
        _log?.Info(LogCategory, $"sneak-cooldown hold released — {why}");
    }

    private void StopCooldownTimer()
    {
        _cooldownTimer?.Stop();
        _cooldownTimer = null;
    }

    private void OnHideInitiate(MatchResult _)
    {
        if (_staleHideAcks > 0)
        {
            _staleHideAcks--;
            _log?.Info(LogCategory, "hide answer ignored — a later command already ended that hide");
            return;
        }
        // Bare "Attempting to hide..." — the server ran a hide check but does NOT
        // report the outcome. Treat it as optimistically hidden; a real hide lands
        // the backstab surprise round and a failed one whiffs (RunIfBackstabFails
        // then flees). Fires for a manual `hid` too, which is correct — we track
        // the game's hide state regardless of who issued the command.
        NoteHideConfirmed();
    }

    private void OnHideFailed(MatchResult _)
    {
        if (_staleHideAcks > 0) _staleHideAcks--;
        // "Attempting to hide...You don't think you are hidden." — the one
        // ground-truth failure signal. Drop the optimistic hidden state.
        if (_stateValue == StealthState.Hidden || _stateValue == StealthState.AttemptingHide)
            Transition(StealthState.Idle);
        _state.IsHidden = false;
    }

    private void Transition(StealthState next)
    {
        if (_stateValue == next) return;
        StealthState prev = _stateValue;
        _stateValue = next;
        _log?.Info(LogCategory, $"state {prev} -> {next}");
        StateChanged?.Invoke(prev, next);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sneakingSub.Dispose();
        _notSneakingSub.Dispose();
        _sneakInitiateSub.Dispose();
        _sneakFailedSub.Dispose();
        _cantSneakSub.Dispose();
        _hideInitiateSub.Dispose();
        _hideFailedSub.Dispose();
        ReleaseCooldownHold("disposed");
        ReleaseSettleHold("disposed");
        _inPlaceTimer?.Stop();
    }
}
