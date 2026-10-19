using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Map;

// State machine that drives the walker's bash → pick → open flow for a
// single door. Mirrors TrapDisarmManager's shape: one request in flight
// at a time, FIFO queue for follow-ups, the walker (or any other caller)
// gets a single terminal DoorOpenResult via callback when the request
// settles.
//
// Decision matrix per request:
//   1) The openers to try, in order, come from DoorPolicy.OpeningOrder: a keyless
//      door's preferred verb (bash, or pick with OtherSettings.PicklocksOverBash)
//      then the other; a keyed door's pick first when the character has Picklocks,
//      then the key if carried, then a bash, then the key not carried. A verb the
//      character can't open it with is left out; nothing left fails at once.
//   2) A plan that starts with a pick sends `open <dir>` first: a door only closed
//      opens, and a locked one goes on to the pick.
//   3) On BASH failure ("fails" line): no cap — the bash was chosen for a door this
//      character can break, so keep swinging until it opens, pausing to rest to
//      rest-max whenever HP falls to the rest trigger (bashing drains HP — see
//      GAME_MECHANICS). A bare key lock's bash is the exception, capped at
//      OtherSettings.MaxPickAttempts. On PICK failure: retry up to MaxPickAttempts.
//      Either way the next opener of the plan follows.
//   4) A bare `(Key: N)` lock is bashed only where it is known to be a door, or not
//      known and nothing in the room answers to the direction word; the game's
//      answer to the bash is learned (BareKeyLockBook).
//   5) Bash success → Opened directly (bash both unlocks AND swings the
//      door). Pick success → open <dir> → on "is now open" → Opened.
//   6) The door turns out not to be where the character stands — the verb drew
//      "Your command had no effect.", or the tracker confirmed another room —
//      → NotHere: no further verb is sent and the caller re-plans. A pick or a bare
//      lock's bash answered that way in the confirmed room goes on with the plan.
//
// Single-pattern matchers: the regex doesn't capture direction so other
// in-flight commands (manual user typing) could in theory match the wrong
// context. The walker funnels through one request at a time and the
// keyed-door FSM inherits the same matchers — practical conflict is
// negligible.
public sealed class DoorOpenManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerStats _stats;
    private readonly Func<int> _maxPickProvider;
    private readonly Func<int> _maxBashStrengthProvider;
    private readonly Func<bool> _picklocksOverBashProvider;
    private readonly Func<int, string?> _itemNameLookup;
    private readonly Func<int, bool> _holdsKeyItem;
    // Rest-interleave for bashing. Bashing a door drains HP (CONFIRMED mechanic,
    // see GAME_MECHANICS), so a bashable door is bashed with rest breaks rather
    // than a fixed attempt cap: _bashRestNeeded is true once HP has fallen to the
    // rest-if-below trigger (pause bashing, let HealthManager rest), _bashRestRecovered
    // true once it climbs back to rest-max (resume). Both null → no rest gating
    // (no wiring): bash proceeds uncapped without pausing.
    private readonly Func<bool>? _bashRestNeeded;
    private readonly Func<bool>? _bashRestRecovered;
    // True while the movement engines hold for a rest or a meditate. Unset → never.
    private Func<bool>? _restHold;
    // The room the tracker is confirmed in, or null while it isn't sure. Unset →
    // the room a door belongs to is never checked.
    private Func<RoomKey?>? _confirmedRoom;
    private readonly LogService? _log;
    private readonly IDisposable _bashOkSub;
    private readonly IDisposable _bashFailSub;
    private readonly IDisposable _bashRefusedSub;
    private readonly IDisposable _pickOkSub;
    private readonly IDisposable _pickFailSub;
    private readonly IDisposable _notLockedSub;
    private readonly IDisposable _openedSub;
    private readonly IDisposable _alreadyOpenSub;
    private readonly IDisposable _lockedSub;
    private readonly IDisposable _keyOkSub;
    private readonly IDisposable _keyUnknownSub;
    private readonly IDisposable _keyUsedUpSub;
    private readonly IDisposable _noEffectSub;
    private readonly IDisposable _notADoorSub;
    private readonly WireSender _wire = new();
    private bool _disposed;

    private readonly Queue<DoorRequest> _queue = new();
    private DoorRequest? _current;
    // The room the in-flight request's door is in: where the tracker was confirmed
    // when the request started. Null when it wasn't sure then (a maze solve opens
    // doors while lost), which leaves the request unchecked.
    private RoomKey? _doorRoom;
    private DoorState _state = DoorState.Idle;
    private string? _verb;                                // "bash" / "pick"
    private int _verbAttempts;
    // The openers still to try for the in-flight request, in DoorPolicy.OpeningOrder's
    // order, and whether its key came first in it.
    private readonly Queue<DoorOpener> _plan = new();
    private bool _keyFirst;
    // The plan starts with a pick, and `open <dir>` goes out once ahead of it.
    private bool _openFirstPending;
    // Sides whose pick drew "Your command had no effect." this session, in the room
    // the request was confirmed in: no lock to pick there. Left out of every later
    // plan, and the route filter keeps off them when the pick was all a door had
    // (DoorOpenManager.IsUnpickable).
    private readonly HashSet<(RoomKey Room, Direction Dir)> _unpickable = new();
    // The same doors by the room each leads into, for the route filter, which sees an
    // exit by where it goes.
    private readonly HashSet<(RoomKey From, RoomKey To)> _unpickableInto = new();

    // The in-flight request's key fitted the lock and so spent a charge (the game
    // charges a use on "unlocked" and on "was not locked" alike): a last-charge line
    // now is about that key.
    private bool _keyChargeSpent;
    // Told when the game shows the key gone: (key name, every copy). Unset → nobody.
    private Action<string, bool>? _keyLost;
    // What is known of bare `(Key: N)` locks, and whether a word would name someone
    // in the room the bash attack could swing at. Unset → a bare lock is never bashed
    // unless the walker says it is known to bash.
    private BareKeyLockBook? _bareLocks;
    private Func<string, bool>? _wordNamesSomeoneHere;
    // The in-flight request's door as a bare key lock: null when it is no bare lock,
    // or the room it is in isn't confirmed.
    private BareLockBash? _bareKind;
    // Whether this request may bash: the walker's word, or for a bare lock what is
    // known of it and, not known, whether a try is safe here.
    private bool _canBash;
    // The bash under way is a try at a bare lock not known to be a door. Until the
    // game answers it as a door it is never re-sent on a silent watchdog: silence
    // could be the bash attack's fight.
    private bool _bashIsTrial;
    // Whether a stat screen has been read, the same test the movement filter's door
    // providers use. Unread, a door with no viable verb is not the door winning.
    private readonly Func<bool> _statsRead;
    // Picks of the current verb run that drew no reply at all within the watchdog.
    private int _pickTimeouts;

    // Response watchdog. The FSM is event-driven on server result lines with no
    // timer of its own, so a bash/pick/open/use that draws a response matching
    // none of the recognised patterns (or a line lost in post-training prompt
    // churn) would sit in its waiting state forever — and the walker, which only
    // un-sticks on this manager's callback, hangs with it (report
    // paradigm-20260812-050055: stuck WaitingBash after a training detour, not
    // moving). Arm a one-shot after each command send; on expiry treat the wait
    // as a miss so a wedge becomes a bounded retry + replan. Optional — until a
    // scheduler is wired it's inert.
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private IDisposable? _verbWatchdog;
    private static readonly TimeSpan VerbResponseTimeout = TimeSpan.FromSeconds(8);

    // Current state, for diagnostics.
    public DoorState CurrentState => _state;

    // Direction of the in-flight request, or null when idle.
    public string? CurrentDirection => _current?.DirectionShort;

    // The room the in-flight request's door is in, for the bug report. Null when
    // idle or when the request started with the tracker unsure of the room.
    public RoomKey? CurrentDoorRoom => _doorRoom;

    // The last key the game showed gone, and the line that did, for the bug report.
    public string? LastKeyLoss { get; private set; }

    // What was decided about bashing the last bare key lock asked for, for the bug
    // report and the log: "1/1790 W: known to bash", "… not known; a bash is tried".
    public string? BareLockDecision { get; private set; }

    // Outstanding queue depth (excludes the in-flight request).
    public int QueueDepth => _queue.Count;

    public DoorOpenManager(
        MessageRouter router,
        PlayerStats stats,
        Func<int> maxPickAttemptsProvider,
        Func<bool> picklocksOverBashProvider,
        Func<int, string?>? itemNameLookup = null,
        Func<int>? maxBashableStrengthProvider = null,
        Func<int, bool>? holdsKeyItem = null,
        Func<bool>? bashRestNeeded = null,
        Func<bool>? bashRestRecovered = null,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null,
        Func<bool>? statsRead = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(maxPickAttemptsProvider);
        ArgumentNullException.ThrowIfNull(picklocksOverBashProvider);
        _router = router;
        _stats = stats;
        _maxPickProvider = maxPickAttemptsProvider;
        _picklocksOverBashProvider = picklocksOverBashProvider;
        _bashRestNeeded = bashRestNeeded;
        _bashRestRecovered = bashRestRecovered;
        // The per-set bash ceiling (MaxStrengthIndex). Absent it, fall back to the
        // conservative constant.
        _maxBashStrengthProvider = maxBashableStrengthProvider
                                   ?? (() => DoorPolicy.UnbashableStrengthThreshold);
        // Default to "no items known" when no ItemNameStore is wired.
        _itemNameLookup = itemNameLookup ?? (_ => null);
        // Default "assume held": the plain use path with no opportunistic
        // floor-grab; AppServices wires the live inventory check.
        _holdsKeyItem = holdsKeyItem ?? (_ => true);
        _log = log;
        _scheduleDelay = scheduleDelay;
        _statsRead = statsRead ?? (() => true);

        _bashOkSub     = _router.Subscribe(KnownPatterns.DoorBashSuccess,      OnBashSuccess);
        _bashFailSub    = _router.Subscribe(KnownPatterns.DoorBashFailure,      OnBashFailure);
        _bashRefusedSub = _router.Subscribe(KnownPatterns.DoorBashRefused,      OnBashRefused);
        _pickOkSub      = _router.Subscribe(KnownPatterns.DoorPickSuccess,      OnPickSuccess);
        _pickFailSub    = _router.Subscribe(KnownPatterns.DoorPickFailure,      OnPickFailure);
        _notLockedSub   = _router.Subscribe(KnownPatterns.DoorPickNotLocked,    OnPickNotLocked);
        _openedSub      = _router.Subscribe(KnownPatterns.DoorOpenedNow,        OnOpenedNow);
        _alreadyOpenSub = _router.Subscribe(KnownPatterns.DoorAlreadyOpen,      OnOpened);
        _lockedSub      = _router.Subscribe(KnownPatterns.DoorIsLocked,         OnIsLocked);
        _keyOkSub       = _router.Subscribe(KnownPatterns.DoorKeyUnlockSuccess, OnKeyUnlockSuccess);
        _keyUnknownSub  = _router.Subscribe(KnownPatterns.DoorKeyUnknown,       OnKeyUnknown);
        _keyUsedUpSub   = _router.Subscribe(KnownPatterns.DoorKeyUsedUp,        OnKeyUsedUp);
        _noEffectSub    = _router.Subscribe(KnownPatterns.CommandNoEffect,      OnNoEffect);
        _notADoorSub    = _router.Subscribe(KnownPatterns.NotADoorOrGate,       OnNotADoor);
    }

    // Bind the wire-sender. Same shape as TrapDisarmManager.SetWireSender
    // — MainWindowVM supplies the gate-wrapped SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisarmWatchdog();
        _bashOkSub.Dispose();
        _bashFailSub.Dispose();
        _bashRefusedSub.Dispose();
        _pickOkSub.Dispose();
        _pickFailSub.Dispose();
        _notLockedSub.Dispose();
        _openedSub.Dispose();
        _alreadyOpenSub.Dispose();
        _lockedSub.Dispose();
        _keyOkSub.Dispose();
        _keyUnknownSub.Dispose();
        _keyUsedUpSub.Dispose();
        _noEffectSub.Dispose();
        _notADoorSub.Dispose();
    }

    // Queue a door-open request. The walker normalises direction to short
    // form ("n" / "ne" / "u") before calling. The callback fires exactly
    // once on terminal state. keyItemId is 0 for plain doors; non-zero
    // indexes the door's key in ItemNameStore. When the door is keyed and
    // has a stat alternative, the manager tries bash/pick first to save
    // the key's limited charges; only falls back to the single-shot
    // use <keyName> <dir> + open <dir> when bash/pick are unviable or
    // exhaust.
    public void Enqueue(
        Direction direction,
        int statRequirement,
        bool canBash,
        int keyItemId,
        string sender,
        Action<DoorOpenResult> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        // Idempotent per-direction: a request for a direction already in
        // flight or waiting in the queue is a duplicate — drop it rather than
        // stacking it. Callers share a single-in-flight await model (the
        // walker keys off one _awaitingDoorOpen flag), so the live request's
        // terminal callback resolves the caller; a stacked duplicate would
        // instead fire a stray verb once the FSM dequeues it in a room we've
        // since moved past, whose non-door reply matches no subscribed
        // pattern and sticks the FSM — stranding every later door behind it.
        if ((_current is { } cur && cur.Direction == direction)
            || _queue.Any(q => q.Direction == direction))
        {
            _log?.Info("Door",
                $"open {DirectionShort(direction)} already in progress — ignoring duplicate (sender={sender}).");
            return;
        }

        _queue.Enqueue(new DoorRequest(direction, statRequirement, canBash, keyItemId, sender, reply));
        _log?.Info("Door",
            $"open {DirectionShort(direction)} queued (sender={sender}, key={keyItemId}, depth={_queue.Count}).");
        TryStartNext();
    }

    // Abort the current request (if any) + drain the queue. Pending
    // callers receive a Failed("flow stopped") reply.
    public void StopAll()
    {
        DisarmWatchdog();
        if (_current is { } cur)
        {
            cur.Reply(new DoorOpenResult.Failed("door flow stopped"));
            _current = null;
        }
        while (_queue.Count > 0)
        {
            DoorRequest q = _queue.Dequeue();
            q.Reply(new DoorOpenResult.Failed("door flow stopped"));
        }
        _state = DoorState.Idle;
        _doorRoom = null;
        _verb = null;
        _verbAttempts = 0;
        _plan.Clear();
        _keyChargeSpent = false;
        _bareKind = null;
        _bashIsTrial = false;
        _log?.Info("Door", "Door flow stopped — queue drained.");
    }

    // ----- request kickoff --------------------------------------------

    private void TryStartNext()
    {
        if (_state != DoorState.Idle) return;
        if (_queue.Count == 0) return;
        _current = _queue.Dequeue();
        _doorRoom = _confirmedRoom?.Invoke();
        _state = DoorState.SelectingVerb;
        _verbAttempts = 0;
        _keyChargeSpent = false;
        DecideBareLockBash();
        StartChosenVerb();
    }

    // The door's side was found to have no lock to pick this session.
    public bool IsUnpickable(RoomKey from, RoomKey to) => _unpickableInto.Contains((from, to));

    // A new session: what a board answered last time is no longer assumed.
    public void ForgetSession()
    {
        _unpickable.Clear();
        _unpickableInto.Clear();
    }

    // A bare `(Key: N)` lock is bashed where it is known to be a door or gate, never
    // where it is known to be a key exit, and otherwise tried only when nothing in
    // the room answers to the direction word: at a key exit the bash is the bash
    // attack, on whoever the word names (GAME_MECHANICS "Locked doors — picking,
    // opening and bashing"). Report paradigm-20261018-192819; user, 2026-10-18.
    private void DecideBareLockBash()
    {
        _bashIsTrial = false;
        _bareKind = null;
        if (_current is not { } cur) return;
        _canBash = cur.CanBash;
        if (_doorRoom is not { } room || _bareLocks?.KindAt(room, cur.Direction) is not { } kind) return;
        _bareKind = kind;
        string side = BareKeyLockBook.FormatSide(room, cur.Direction);
        string word = cur.DirectionShort;
        switch (kind)
        {
            case BareLockBash.Bashable:
                _canBash = true;
                BareLockDecision = $"{side}: known to bash (a door or gate)";
                break;
            case BareLockBash.NotBashable:
                _canBash = false;
                BareLockDecision = $"{side}: known not to bash (a key exit); key or pick";
                break;
            default:
                bool named = _wordNamesSomeoneHere?.Invoke(word) ?? true;
                _canBash = !named;
                _bashIsTrial = !named;
                BareLockDecision = named
                    ? $"{side}: not known whether it bashes; not tried, since 'bash {word}' could be taken for someone here"
                    : $"{side}: not known whether it bashes; a bash is tried (nobody here answers to '{word}')";
                break;
        }
        _log?.Info("Door", $"bare key lock {BareLockDecision}.");
    }

    // The game answered a bash at a bare lock: as a door (a bash or a miss), or as no
    // door at all (the bash attack's refusals and "no effect").
    private void LearnBareLock(bool bashable, string because)
    {
        if (_current is not { } cur || _bareKind is null || _doorRoom is not { } room) return;
        _bashIsTrial = false;
        _bareKind = bashable ? BareLockBash.Bashable : BareLockBash.NotBashable;
        if (!bashable) _canBash = false;
        if (_bareLocks?.Learn(room, cur.Direction, bashable) != true) return;
        _log?.Info("Door", $"learned that {BareKeyLockBook.FormatSide(room, cur.Direction)} "
            + (bashable ? "is a door that bashes" : "is a key exit that doesn't bash") + $" ({because}).");
    }

    private void StartChosenVerb()
    {
        if (_current is not { } cur) return;

        _plan.Clear();
        bool keyed = cur.KeyItemId > 0;
        int picks = _doorRoom is { } room && _unpickable.Contains((room, cur.Direction)) ? 0 : _stats.Picklocks;
        foreach (DoorOpener step in DoorPolicy.OpeningOrder(
                     cur.StatRequirement, _canBash, _stats.Strength, picks,
                     keyed, keyed && _holdsKeyItem(cur.KeyItemId), _picklocksOverBashProvider(),
                     _maxBashStrengthProvider()))
            _plan.Enqueue(step);
        _keyFirst = _plan.TryPeek(out DoorOpener first) && first == DoorOpener.Key;
        if (_plan.Count == 0)
        {
            // Before a stat screen is read the numbers are 0, so any numbered door has
            // no viable verb: that says nothing about the door, and it must not be
            // given up on for good.
            FailCurrent($"no viable open verb (req {cur.StatRequirement}, str {_stats.Strength}, picks {_stats.Picklocks}, canBash {_canBash})",
                unopenable: _statsRead());
            return;
        }
        _log?.Info("Door", $"door {cur.DirectionShort}: trying {string.Join(", then ", _plan).ToLowerInvariant()}.");
        // A pick at a door only closed, not locked, can't succeed (Stock `_cmd_picklock`
        // picks only a locked door or gate), so a plan that starts with picking opens
        // first: a door that isn't locked just opens (user, 2026-10-18).
        _openFirstPending = first == DoorOpener.Pick;
        NextStep();
    }

    // Start the next opener of the plan; the plan run out ends the request.
    // unopenable: whether the last failure was the door winning, rather than the
    // game's replies lost or a key tried first turning out missing.
    private void NextStep(string? reason = null, bool unopenable = true)
    {
        if (_current is not { } cur) return;
        if (!_plan.TryDequeue(out DoorOpener step))
        {
            FailCurrent(reason is null ? "nothing left to try" : $"{reason}; nothing left to try", unopenable);
            return;
        }
        if (reason is not null) _log?.Info("Door", $"{reason}; trying {step.ToString().ToLowerInvariant()} on door {cur.DirectionShort}.");
        switch (step)
        {
            case DoorOpener.Key: StartUseKey(); break;
            case DoorOpener.Pick: StartVerb("pick"); break;
            default: StartVerb("bash"); break;
        }
    }

    // The open tried ahead of the first pick was answered "locked", or not at all:
    // the pick goes out.
    private void PickAfterOpenFirst()
    {
        _state = DoorState.WaitingPick;
        SendVerb();
    }

    private void StartUseKey()
    {
        if (_current is not { } cur) return;
        if (AbandonIfLeftRoom()) return;
        string? keyName = _itemNameLookup(cur.KeyItemId);
        if (string.IsNullOrWhiteSpace(keyName))
        {
            FailCurrent($"key item {cur.KeyItemId} not in active item table");
            return;
        }
        // Opportunistic floor grab: a keyed door often sits in the same room as
        // the mob/chest that yields its key. When we're not carrying this key,
        // try to pick a copy up from the current room before using it — `get` is
        // a no-op ("You don't see that here.") when the floor has none, so the
        // plain use path is never worsened, and a blind `use` on a key we don't
        // hold was the reported failure. Sent ahead of the `use` so the server
        // processes them in order and the key is in hand when `use` runs.
        if (!_holdsKeyItem(cur.KeyItemId))
        {
            _wire.Send($"get {keyName}");
            _log?.Info("Door", $"get {keyName} (not carried — grab from room before use).");
        }
        _state = DoorState.WaitingUseKey;
        _wire.Send($"use {keyName} {cur.DirectionShort}");
        _log?.Info("Door",
            $"use {keyName} {cur.DirectionShort} (single-shot — keys have limited charges).");
        ArmWatchdog();
    }

    private void StartVerb(string verb)
    {
        if (_current is not { } cur) return;
        _verb = verb;
        _verbAttempts = 0;
        _pickTimeouts = 0;
        _state = verb == "bash" ? DoorState.WaitingBash : DoorState.WaitingPick;
        SendVerb();
    }

    private void SendVerb()
    {
        if (_current is not { } cur || _verb is null) return;
        if (AbandonIfLeftRoom()) return;

        // Bashing drains HP — pause before a swing once HP has fallen to the rest
        // trigger and let HealthManager rest to rest-max. NotifyHealthChanged (or the
        // watchdog re-check) resumes once recovered. No attempt cap: DoorPolicy only
        // chose bash for a door this character can break, so we keep swinging (with
        // rest breaks) until it opens.
        if (_verb == "bash" && _bashRestNeeded?.Invoke() == true)
        {
            _state = DoorState.WaitingBashRest;
            _log?.Info("Door",
                $"bash {cur.DirectionShort} paused for rest — HP at/below rest trigger.");
            ArmWatchdog();   // periodic re-check in case a HP-change notification is missed
            return;
        }

        // A rest started for any other reason (a fight on the way to the door, a
        // meditate): bash and pick both stand a resting character up (GAME_MECHANICS
        // "What ends a rest"), so the next try waits the hold out rather than break
        // a rest that would only have to start again.
        if (_restHold?.Invoke() == true)
        {
            _state = DoorState.WaitingRestHold;
            _log?.Info("Door", $"{_verb} {cur.DirectionShort} held — resting; trying once the rest is over.");
            ArmWatchdog();   // periodic re-check in case the hold-change notification is missed
            return;
        }

        if (_verb == "pick" && _openFirstPending)
        {
            _openFirstPending = false;
            _state = DoorState.WaitingOpenFirst;
            _wire.Send($"open {cur.DirectionShort}");
            _log?.Info("Door", $"open {cur.DirectionShort} (ahead of the pick: a door not locked needs none).");
            ArmWatchdog();
            return;
        }

        _verbAttempts++;
        _wire.Send($"{_verb} {cur.DirectionShort}");
        if (_verb == "bash")
            _log?.Info("Door", $"bash {cur.DirectionShort} (uncapped, attempt {_verbAttempts}).");
        else
            _log?.Info("Door",
                $"pick {cur.DirectionShort} (attempt {_verbAttempts}/{_maxPickProvider()}).");
        ArmWatchdog();
    }

    // Recovery topped off during a bash rest-pause — resume swinging. Re-enters
    // WaitingBash and sends the next bash (SendVerb re-checks the rest gate, now
    // satisfied).
    private void ResumeBashAfterRest()
    {
        if (_current is not { } cur || _state != DoorState.WaitingBashRest) return;
        _log?.Info("Door", $"rest complete — resuming bash {cur.DirectionShort}.");
        _state = DoorState.WaitingBash;
        SendVerb();
    }

    public void SetRestHold(Func<bool> isHeld) => _restHold = isHeld;

    // Bound after construction: the tracker is built later than this manager.
    public void SetConfirmedRoomProbe(Func<RoomKey?> confirmedRoom) => _confirmedRoom = confirmedRoom;

    // Told when the game shows a key the request used to be gone: (key name, every
    // copy). `You do not have <key>.` means no copy is left; a last-charge line, that
    // the copy just used is gone. Bound after construction: the inventory is built later.
    public void SetKeyLossSink(Action<string, bool> keyLost) => _keyLost = keyLost;

    // wordNamesSomeoneHere: whether a word could be taken by the bash attack for
    // someone in the room (BashTargetWord), true too while the room's roster isn't
    // known. Bound after construction: the room's roster is built later.
    public void SetBareLockBook(BareKeyLockBook book, Func<string, bool> wordNamesSomeoneHere)
    {
        _bareLocks = book;
        _wordNamesSomeoneHere = wordNamesSomeoneHere;
    }

    // The rest hold changed: a bash or pick that was waiting on it goes out once
    // it's over.
    public void NotifyRestHoldChanged()
    {
        if (_disposed || _state != DoorState.WaitingRestHold || _current is not { } cur) return;
        if (_restHold?.Invoke() == true) return;
        _log?.Info("Door", $"rest over — resuming {_verb} {cur.DirectionShort}.");
        _state = _verb == "bash" ? DoorState.WaitingBash : DoorState.WaitingPick;
        SendVerb();
    }

    // Live-HP change hook — AppServices subscribes PlayerState.Hp and calls this.
    // While a bash is paused for rest, resume the moment HP climbs back to rest-max
    // instead of waiting on the watchdog's periodic re-check. No-op otherwise.
    public void NotifyHealthChanged()
    {
        if (_disposed || _state != DoorState.WaitingBashRest) return;
        if (_bashRestRecovered?.Invoke() == true) ResumeBashAfterRest();
    }

    private void SendOpen()
    {
        if (_current is not { } cur) return;
        if (AbandonIfLeftRoom()) return;
        _state = DoorState.WaitingOpen;
        _wire.Send($"open {cur.DirectionShort}");
        _log?.Info("Door", $"open {cur.DirectionShort} (after pick success).");
        ArmWatchdog();
    }

    // Arm (replacing any prior) the response watchdog for the command just sent.
    // Every send site re-arms and every terminal transition disarms via Reset, so
    // at most one timer is ever live for the in-flight request.
    private void ArmWatchdog()
    {
        _verbWatchdog?.Dispose();
        _verbWatchdog = _scheduleDelay?.Invoke(VerbResponseTimeout, OnVerbTimeout);
    }

    private void DisarmWatchdog()
    {
        _verbWatchdog?.Dispose();
        _verbWatchdog = null;
    }

    // No result line arrived for the in-flight door command within the window.
    // Recover rather than hang: for a bash/pick, resend up to the attempt cap
    // then fall back / fail; for the open/use-key follow-up, fail cleanly so the
    // walker replans. A late-but-real reply that lands first leaves us Idle, so
    // this is a no-op then.
    private void OnVerbTimeout()
    {
        if (_disposed || _current is not { } cur) return;
        if (AbandonIfLeftRoom()) return;
        switch (_state)
        {
            case DoorState.WaitingBash:
                if (_bashIsTrial)
                {
                    NextStep("a bash tried at a bare key lock drew no door's answer");
                    return;
                }
                // Uncapped — a lost bash response is just re-sent (SendVerb pauses
                // for rest first when HP is low). No fall-back to pick.
                _log?.Info("Door",
                    $"bash {cur.DirectionShort}: no response in {VerbResponseTimeout.TotalSeconds:0}s " +
                    $"(attempt {_verbAttempts}) — treating as a miss, re-bashing.");
                SendVerb();   // re-arms
                return;
            case DoorState.WaitingBashRest:
                // Periodic recovery re-check while resting — resume the bash once HP
                // has climbed back to rest-max, otherwise keep waiting.
                if (_bashRestRecovered?.Invoke() == true) ResumeBashAfterRest();
                else ArmWatchdog();
                return;
            case DoorState.WaitingRestHold:
                if (_restHold?.Invoke() == true) ArmWatchdog();
                else NotifyRestHoldChanged();
                return;
            case DoorState.WaitingPick:
                int cap = _maxPickProvider();
                _log?.Info("Door",
                    $"pick {cur.DirectionShort}: no response in {VerbResponseTimeout.TotalSeconds:0}s " +
                    $"(attempt {_verbAttempts}/{cap}) — treating as a miss.");
                _pickTimeouts++;
                if (_verbAttempts < cap) { SendVerb(); return; }   // re-arms
                // Every pick unanswered is a lost reply, not the lock winning.
                NextStep("pick timed out with no response", unopenable: _pickTimeouts != _verbAttempts);
                return;
            case DoorState.WaitingOpenFirst:
                PickAfterOpenFirst();
                return;
            case DoorState.WaitingOpen:
            case DoorState.WaitingUseKey:
                FailCurrent($"{_state.ToString().ToLowerInvariant()} timed out with no response");
                return;
        }
    }

    // ----- message handlers -------------------------------------------

    private void OnBashSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingBash) return;
        if (_current is null) return;
        _log?.Info("Door", $"bash {_current.DirectionShort} succeeded.");
        LearnBareLock(bashable: true, "the bash opened it");
        RaiseOpenedByUs(byBash: true);
        SucceedCurrent();
    }

    private void OnBashFailure(MatchResult _)
    {
        if (_state != DoorState.WaitingBash) return;
        if (_current is null) return;
        LearnBareLock(bashable: true, "the bash missed, as at a door");
        // A bare key lock's bash is capped at the pick tries: it bashes at Strength
        // only, and a side that won't give is gone round instead (user, 2026-10-18).
        if (_bareKind is not null && _verbAttempts >= _maxPickProvider())
        {
            NextStep($"bash missed {_verbAttempts} times at a bare key lock");
            return;
        }
        // A bashable door is bashed until it opens — no cap, no fall-back to pick.
        // SendVerb pauses for rest first when HP is low, so the retries pace
        // themselves around the rest cycle rather than a fixed attempt count.
        SendVerb();
    }

    // The server refused the bash outright (no weapon in hand / no bash ability).
    // Re-bashing can only get the same answer — and with no pattern for it the
    // watchdog used to re-bash forever — so try pick, then the key.
    private void OnBashRefused(MatchResult m)
    {
        if (_state != DoorState.WaitingBash) return;
        if (_current is null) return;
        // At a door the bash never asks for a weapon or the skill: these come only
        // from the bash attack, so a bare lock that draws them is a key exit.
        LearnBareLock(bashable: false, $"'{m.Text.Trim()}'");
        NextStep($"can't bash ('{m.Text.Trim()}')");
    }

    // The game saying the exit that way is no door in the room the character is
    // standing in (GAME_MECHANICS "Locked doors — picking, opening and bashing"):
    //   * "Your command had no effect." to a bash or a pick;
    //   * "That is not a door or a gate!" to an open.
    // Either the character isn't in the room the request was planned from or the
    // map has the exit wrong, and the same verb can only draw the same answer —
    // unrecognised, it read as silence and the watchdog re-bashed without end
    // (report paradigm-20260924-053941).
    //
    // The first line answers any command the game refuses, so it is the door's only
    // while a bash or a pick is the one awaiting a reply, and never when the command
    // echoed ahead of it is some other one (a cast or an attack sent meanwhile).
    private void OnNoEffect(MatchResult _)
    {
        if (_current is not { } cur) return;
        if (_state is not (DoorState.WaitingBash or DoorState.WaitingPick)) return;
        string awaited = $"{_verb} {cur.DirectionShort}";
        if (_router.ReplyIsForCommandNotNaming(awaited))
        {
            _log?.Debug("Door",
                $"'no effect' answers '{_router.CommandEchoedBeforeLine}', not {awaited} — still waiting.");
            return;
        }
        // A bare lock's bash that had no effect, in the room confirmed to hold it: the
        // bash attack found nobody, so the side is a key exit, opened by key or pick.
        if (_state == DoorState.WaitingBash && _bareKind is not null)
        {
            LearnBareLock(bashable: false, "'Your command had no effect.'");
            NextStep($"'{awaited}' had no effect: a key exit, not a door to bash");
            return;
        }
        // A pick that had no effect in the room confirmed to hold the door: there's no
        // lock to pick that way. Abandoned as NotHere, the walker planned through the
        // same door again until its re-plans ran out; the pick is now left out of this
        // door for the session and the rest of the plan tried.
        if (_state == DoorState.WaitingPick && _doorRoom is { } room
            && _confirmedRoom?.Invoke() == room)
        {
            _unpickable.Add((room, cur.Direction));
            if (_bareLocks?.ExitAt(room, cur.Direction) is { } exit) _unpickableInto.Add((room, exit.Target));
            _log?.Info("Door", $"no lock to pick at {BareKeyLockBook.FormatSide(room, cur.Direction)} — left out of picks this session.");
            NextStep($"'{awaited}' had no effect");
            return;
        }
        AbandonCurrent($"'{awaited}' had no effect, so that exit is not a door here");
    }

    // Only an open or a close prints this one, so it needs no echo to tell whose
    // it is beyond an open being what the request is waiting on. An open the user
    // typed for some other way while the request waits is told apart by its echo.
    private void OnNotADoor(MatchResult _)
    {
        if (_current is not { } cur || _state is not (DoorState.WaitingOpen or DoorState.WaitingOpenFirst)) return;
        string awaited = $"open {cur.DirectionShort}";
        if (_router.ReplyIsForCommandNotNaming(awaited)) return;
        AbandonCurrent($"'{awaited}' was answered \"That is not a door or a gate!\"");
    }

    private void OnPickSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingPick) return;
        if (_current is null) return;
        _log?.Info("Door", $"pick {_current.DirectionShort} succeeded — sending open.");
        SendOpen();
    }

    private void OnPickFailure(MatchResult _)
    {
        if (_state != DoorState.WaitingPick) return;
        if (_current is null) return;
        int cap = _maxPickProvider();
        if (_verbAttempts < cap)
        {
            SendVerb();
            return;
        }
        NextStep("pick exhausted");
    }

    private void OnPickNotLocked(MatchResult _)
    {
        if (_current is null) return;
        // "The door was not locked." — the door needs no unlock, whichever
        // breach verb we were mid-attempt on. A keyed door's use-key path
        // (WaitingUseKey) and a bash both hit this same line on an
        // already-unlocked door; gating only on WaitingPick stranded the FSM
        // there, so the walker stalled until a manual room reparse. Skip
        // straight to open — an already-open door then resolves via OnOpened.
        if (_state is DoorState.WaitingPick
                   or DoorState.WaitingBash
                   or DoorState.WaitingUseKey)
        {
            _log?.Info("Door",
                $"door {_current.DirectionShort} wasn't locked — sending open (was {_state}).");
            if (_state == DoorState.WaitingUseKey) _keyChargeSpent = true;
            SendOpen();
        }
    }

    // Our own `open` opened it (not "already open": someone else did, at a time we
    // don't know).
    private void OnOpenedNow(MatchResult m)
    {
        if (_state is DoorState.WaitingOpen or DoorState.WaitingOpenFirst) RaiseOpenedByUs(byBash: false);
        OnOpened(m);
    }

    // Told the side and how, when the request's own command opened the door, in the
    // room confirmed to hold it (DoorsHeldOpen).
    public event Action<RoomKey, Direction, bool>? OpenedByUs;

    private void RaiseOpenedByUs(bool byBash)
    {
        if (_current is not { } cur || _doorRoom is not { } room) return;
        if (_confirmedRoom?.Invoke() is { } now && now != room) return;
        OpenedByUs?.Invoke(room, cur.Direction, byBash);
    }

    private void OnOpened(MatchResult _)
    {
        if (_current is null) return;
        // "is now open" / "already open" — the door is in the
        // desired state regardless of which verb just landed on the
        // wire. Accept from any active waiting state so a bash on an
        // already-open door (server replies "The door is already
        // open.") doesn't strand the FSM waiting for a
        // "bashed the door open" line that will never arrive.
        // Without this, the walker stalls indefinitely on the very
        // next door after a successful bash on the previous one.
        if (_state is DoorState.WaitingBash
                   or DoorState.WaitingBashRest
                   or DoorState.WaitingRestHold
                   or DoorState.WaitingPick
                   or DoorState.WaitingOpen
                   or DoorState.WaitingOpenFirst
                   or DoorState.WaitingUseKey)
        {
            _log?.Info("Door",
                $"door {_current.DirectionShort} confirmed open (was {_state}).");
            SucceedCurrent();
        }
    }

    private void OnKeyUnlockSuccess(MatchResult _)
    {
        if (_state != DoorState.WaitingUseKey) return;
        if (_current is null) return;
        _log?.Info("Door", $"key unlock {_current.DirectionShort} succeeded — sending open.");
        _keyChargeSpent = true;
        SendOpen();
    }

    // The key didn't open the lock: missing (`You do not have <key>.` on Paradigm,
    // `You don't have <key> <dir>.` on Stock) or the wrong one (`… doesn't seem to fit
    // that lock.`) — terminal, since a key is single-shot and the use is not retried.
    // A "have" line answers any `use`, so it is the door's only when it names this
    // key. A missing key is dropped from what is held, or the planner would route
    // through its door again on a key the game says isn't there (report
    // paradigm-20261018-192819: unread, the line left the request to its 8 s
    // watchdog, and the pack list kept a key whose last charge had gone). The key is
    // named: this reason is what the walk fails with, and it is all the user is told
    // about why a walk that went to fetch a key stopped at the door.
    private void OnKeyUnknown(MatchResult m)
    {
        if (_state != DoorState.WaitingUseKey) return;
        if (_current is not { } cur) return;
        string? keyName = _itemNameLookup(cur.KeyItemId);
        string text = m.Text.Trim();
        bool notHeld = IsHaveLine(text);
        if (notHeld && (keyName is not { Length: > 0 } || !NamesTheKey(text, keyName)))
        {
            _log?.Debug("Door", $"'{text}' names another item than the {keyName ?? $"item #{cur.KeyItemId}"} — still waiting.");
            return;
        }
        string key = keyName is { Length: > 0 } ? keyName : $"item #{cur.KeyItemId}";
        if (notHeld) NoteKeyLost(cur.KeyItemId, key, everyCopy: true, text);
        // A key tried first and missing isn't the door winning: a walk that went to
        // fetch it stops there, naming the key.
        NextStep($"use-key failed (the {key} is missing or wrong)", unopenable: !_keyFirst);

        static bool IsHaveLine(string line) =>
            line.StartsWith("You do not have ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You don't have ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You dont have ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You have no ", StringComparison.OrdinalIgnoreCase);

        // The words after "have" / "have no" are the key's whole name, with Stock's
        // typed direction after it, so the "iron key" of a missing "large iron key"
        // isn't taken for it.
        static bool NamesTheKey(string line, string key)
        {
            int at = line.IndexOf(" have ", StringComparison.OrdinalIgnoreCase);
            string rest = line[(at + " have ".Length)..].TrimEnd('.', '!', ' ');
            if (rest.StartsWith("no ", StringComparison.OrdinalIgnoreCase)) rest = rest[3..];
            if (rest.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            return rest.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)
                && DirectionExtensions.TryFromToken(rest[(key.Length + 1)..], out _);
        }
    }

    // The key this request used spent its last charge and left the pack (GAME_MECHANICS
    // "Item charges (`Uses` / `UseCount`)"). The line comes straight after the
    // unlock, while the request waits on its `open`.
    private void OnKeyUsedUp(MatchResult m)
    {
        if (_current is not { } cur || !_keyChargeSpent || cur.KeyItemId <= 0) return;
        string key = _itemNameLookup(cur.KeyItemId) is { Length: > 0 } name ? name : $"item #{cur.KeyItemId}";
        string text = m.Text.Trim();
        // The engine's own fallback names the item: one naming another isn't ours.
        if (text.StartsWith("It's uses gone", StringComparison.Ordinal)
            && !text.Equals($"It's uses gone, {key} disappears from your inventory!", StringComparison.OrdinalIgnoreCase)) return;
        _keyChargeSpent = false;
        NoteKeyLost(cur.KeyItemId, key, everyCopy: false, text);
    }

    private void NoteKeyLost(int keyItemId, string key, bool everyCopy, string line)
    {
        LastKeyLoss = $"{key} (#{keyItemId}){(everyCopy ? ", none held" : ", a copy used up")}: \"{line}\"";
        _log?.Info("Door", everyCopy
            ? $"the game says the {key} isn't held — dropping it from what is held."
            : $"the {key} spent its last charge — dropping the copy used from what is held.");
        // A key the item table doesn't name can't be found in what is held either.
        if (_itemNameLookup(keyItemId) is { Length: > 0 } name) _keyLost?.Invoke(name, everyCopy);
    }

    private void OnIsLocked(MatchResult _)
    {
        if (_current is null) return;

        // The open tried before any pick: it is locked, so the plan goes on.
        if (_state == DoorState.WaitingOpenFirst)
        {
            PickAfterOpenFirst();
            return;
        }

        // Plain door open path hit a locked door. If a key is
        // available, swing into the key sequence; otherwise fail.
        if (_state == DoorState.WaitingOpen)
        {
            if (_current.KeyItemId > 0)
            {
                _log?.Info("Door",
                    $"open {_current.DirectionShort}: door is locked — swinging to key sequence.");
                StartUseKey();
                return;
            }
            FailCurrent("door is locked (no key available)");
        }
    }

    // ----- terminal transitions ---------------------------------------

    private void SucceedCurrent()
    {
        if (_current is not { } cur) return;
        if (AbandonIfLeftRoom()) return;
        cur.Reply(DoorOpenResult.Opened.Instance);
        Reset();
    }

    // unopenable: bash and pick are both out for this character (DoorOpenResult.Failed).
    // The request is cleared before the caller hears of it for the reason
    // AbandonCurrent gives: the caller may re-plan from inside the reply and ask for
    // another door at once.
    private void FailCurrent(string reason, bool unopenable = false)
    {
        if (_current is not { } cur) return;
        if (AbandonIfLeftRoom()) return;
        _log?.Warn("Door", $"door {cur.DirectionShort} failed: {reason}.");
        ClearCurrent();
        cur.Reply(new DoorOpenResult.Failed(reason, unopenable));
        TryStartNext();
    }

    // The tracker has confirmed another room than the one this door is in: a move
    // already on the wire landed, or the request was planned from a room the
    // character had already left. Any further verb would be aimed at that room's
    // exit, and an "opened" or "failed" line now would be about some other door, so
    // the request ends here whatever it was about to do. Checked where the FSM acts
    // (each send, each outcome, each watchdog expiry) rather than on the tracker's
    // own event: ending a request re-plans the caller's walk, and doing that from
    // inside the tracker's dispatch would run ahead of the caller's own handler for
    // the same transition.
    private bool AbandonIfLeftRoom()
    {
        if (_current is not { } cur || _doorRoom is not { } doorRoom) return false;
        if (_confirmedRoom?.Invoke() is not { } now || now == doorRoom) return false;
        AbandonCurrent($"now in {now}, and the door {cur.DirectionShort} was in {doorRoom}");
        return true;
    }

    private void AbandonCurrent(string reason)
    {
        if (_current is not { } cur) return;
        _log?.Info("Door",
            $"door {cur.DirectionShort} abandoned — {reason}; handing back for a re-check of the room.");
        // The request is cleared before the caller hears of it: the caller re-plans
        // from inside the reply and may ask for a door the same way at once, and a
        // request still standing would have the duplicate guard swallow the new one,
        // leaving the caller waiting on a reply that never comes.
        ClearCurrent();
        cur.Reply(new DoorOpenResult.NotHere(reason));
        TryStartNext();
    }

    private void Reset()
    {
        ClearCurrent();
        TryStartNext();
    }

    private void ClearCurrent()
    {
        DisarmWatchdog();
        _current = null;
        _doorRoom = null;
        _state = DoorState.Idle;
        _verb = null;
        _verbAttempts = 0;
        _plan.Clear();
        _keyFirst = false;
        _openFirstPending = false;
        _keyChargeSpent = false;
        _bareKind = null;
        _bashIsTrial = false;
    }

    // ----- helpers ----------------------------------------------------

    internal static string DirectionShort(Direction d) => d switch
    {
        Direction.N => "n",
        Direction.S => "s",
        Direction.E => "e",
        Direction.W => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U => "u",
        Direction.D => "d",
        _ => "?",
    };

    // Public FSM state, for diagnostics.
    public enum DoorState
    {
        // No request in flight.
        Idle,
        // Picking the first verb based on player stats + settings.
        SelectingVerb,
        // Sent bash <dir>; awaiting success/failure line.
        WaitingBash,
        // Bashing paused mid-sequence: HP fell to the rest trigger, resting to
        // rest-max before the next swing (NotifyHealthChanged / watchdog resumes).
        WaitingBashRest,
        // The next bash or pick is waiting for a rest hold (not our own bash rest)
        // to end.
        WaitingRestHold,
        // Sent pick <dir>; awaiting success/failure line.
        WaitingPick,
        // Pick succeeded (or door was unlocked); sent open <dir>; awaiting opened line.
        WaitingOpen,
        // Sent use <keyName> <dir>; awaiting unlock-success / key-missing.
        WaitingUseKey,
        // Sent open <dir> ahead of picking; awaiting opened (done) or locked (pick).
        WaitingOpenFirst,
    }

    private sealed record DoorRequest(
        Direction Direction,
        int StatRequirement,
        bool CanBash,
        int KeyItemId,
        string Sender,
        Action<DoorOpenResult> Reply)
    {
        public string DirectionShort => DoorOpenManager.DirectionShort(Direction);
    }
}
