using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps a checkspell hazard buff up while the walker traverses a hazard room.
//
// Some room hazards (the Scorching Desert's heat, the underwater drown) are
// survived not by HOLDING a counter but by an active buff the player raises with
// `use <item>` — the desert waterskin `use`s to cast buff 711, safe only while
// that buff is up. Carrying the source item is enough to let the route pass the
// hazard gate (RoomHazardIndex.RoomHazard.IsSatisfiedBy checks carrying), but the
// buff must actually be RAISED, or the route walks straight into the damage — the
// "it required a waterskin but never used it" report.
//
// This engine closes that gap two ways:
//
//  • Predictive (timer) — on the walker's approach hook (fired the instant a step
//    is committed, before the move bytes, so the `use` lands the buff before we
//    arrive) it resolves the room's hazard and, for each checkspell counter whose
//    source item we carry, `use`s it — but only when the buff would have lapsed. A
//    per-source-item timer keyed on the buff's data-driven duration debounces the
//    re-use: a fast traverse of a long hazard stretch spends ONE charge, and a
//    stretch outlasting the buff re-raises it once the window closes. Charges are
//    finite (a fresh waterskin holds 3; players carry two or three), so the timer
//    keeps it from burning one per room. This is the PRIMARY refresh, because the
//    waterskin buff ships no wear-off message to react to — its lapse can only be
//    predicted from the duration.
//  • Reactive (message) — the timer only estimates the lapse. When the estimate is
//    off and the buff drops early, the room casts its lapse-damage spell and the
//    game emits that spell's message ("you suffer in the desert heat... you need
//    water, soon!"). Seeing that line, we fire exactly ONE `use` to re-raise. The
//    trigger is the game-data message record linked to the hazard's lapse spell
//    (RoomHazardIndex.BuffCounter.LapseSpell), not hardcoded realm text, so it
//    tracks whatever the active set says. We then wait for the `use`'s own
//    confirmation line (the buff spell's caster message — the swig); if a SECOND
//    lapse prompt arrives before that swig, the `use` drew nothing (out of charges
//    / waterskins) and we HALT the walk rather than march deeper into a hazard we
//    can no longer counter.
//
// A party FOLLOWER runs no walk of its own — the leader's route carries it — so
// neither hook above would ever fire for it, and followers crossed the desert
// taking heat damage while the leader drank (a party had to hand-write a
// "You suffer in the desert heat... → use waterskin" trigger). While following,
// arriving in a hazard room raises the buff (a follower can't see the leader's
// next step, so it can't pre-empt; the per-item timer still spends one charge per
// buff window), and the lapse prompt re-raises the same as on a walk.
//
// No master toggle: surviving a hazard room the route already commits to walking
// is not opt-in (mirrors auto-light's "leave it off if you don't want it" — here
// the equivalent is simply not routing through the hazard). It only ever acts when
// a checkspell hazard and a carried source item coincide during a live walk — ours,
// or the leader's we're following.
public sealed class AutoHazardCounterProvisioner
{
    // LogService category — [HazardCounter] rows per buff raise / skip.
    public const string LogCategory = "HazardCounter";

    // Re-raise the buff this many seconds BEFORE its computed expiry, so a step
    // into the next hazard room never lands in the gap between lapse and refresh.
    private const int RefreshMarginSeconds = 15;

    // Fallback refresh interval when the buff's duration isn't in the data (Dur 0):
    // still re-use periodically rather than once-and-never so an un-timed counter
    // doesn't silently lapse.
    private const int UnknownDurationRefreshSeconds = 60;

    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int, RoomHazardIndex.RoomHazard?> _hazardForSpell;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    // Resolve a spell number to a predicate that recognises that spell's own
    // game-data message on a server line (lapse prompt for LapseSpell, swig
    // confirmation for BuffSpell). Null → the reactive path stays inert and only
    // the predictive timer keeps the buff up.
    private readonly Func<int, Func<string, bool>?>? _messageMatcherForSpell;
    private readonly Func<bool> _walkActive;
    private readonly Func<bool> _followingLeader;
    private readonly Action<string>? _haltWalk;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Per buff-source item id: when we last `use`d it. The refresh window is the
    // buff's duration minus the margin; a use inside that window is a no-op.
    private readonly Dictionary<int, DateTimeOffset> _lastUsed = new();

    // The hazard counter we last approached, with its compiled lapse / swig line
    // predicates — armed so a lapse prompt fired mid-room (not just on a step) is
    // still caught. Null when we're not in/around a checkspell hazard.
    private RoomHazardIndex.BuffCounter? _activeCounter;
    private Func<string, bool>? _lapseMatch;
    private Func<string, bool>? _swigMatch;

    // A `use` was sent and its swig confirmation hasn't been seen yet. A lapse
    // prompt arriving while this is set means the `use` produced nothing — out of
    // charges — so the walk halts instead of re-firing into the void.
    private bool _awaitingSwig;

    // We've already told the room we're out of the buff's source item. Cleared once a
    // `use` goes out again (a fresh one picked up / handed over), so a later run-out
    // is announced anew — but a hazard stretch doesn't repeat it every lapse tick.
    private bool _announcedOut;

    public AutoHazardCounterProvisioner(
        Func<RoomKey, Room?> resolveRoom,
        Func<int, RoomHazardIndex.RoomHazard?> hazardForSpell,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<int, Func<string, bool>?>? messageMatcherForSpell = null,
        Func<bool>? walkActive = null,
        Action<string>? haltWalk = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null,
        Func<bool>? followingLeader = null)
    {
        ArgumentNullException.ThrowIfNull(resolveRoom);
        ArgumentNullException.ThrowIfNull(hazardForSpell);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        _resolveRoom = resolveRoom;
        _hazardForSpell = hazardForSpell;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _messageMatcherForSpell = messageMatcherForSpell;
        _walkActive = walkActive ?? (() => true);
        _followingLeader = followingLeader ?? (() => false);
        _haltWalk = haltWalk;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel. Until bound, a `use` is recorded for tests only.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Predictive one-room-lookahead buff. The walker / loop-runner call this the
    // instant they commit to a step, with the room about to be entered, BEFORE the
    // move bytes go out. If that room's cast-on-enter spell is a checkspell hazard
    // whose buff source we carry, we `use` it now — so the buff is up when the step
    // lands. A no-op for a seeable / benign / passive-counter room; the per-item
    // timer skips a re-use while the buff is still covering us.
    public void OnApproachingRoom(RoomKey target)
    {
        if (_resolveRoom(target) is not { } room) return;
        if (room.Spell <= 0) return;
        if (_hazardForSpell(room.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
        {
            Arm(counter);
            TryRaiseBuff(counter);
        }
    }

    // A follower just arrived in a room (the leader's move carried it). Raise the
    // room's hazard buff the same way the approach hook does for our own walk. Our
    // own walk, if one is running, already covered this room on approach.
    public void OnArrivedInRoom(RoomKey room)
    {
        if (!_followingLeader() || _walkActive()) return;
        if (_resolveRoom(room) is not { } r || r.Spell <= 0) return;
        if (_hazardForSpell(r.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
        {
            Arm(counter);
            TryRaiseBuff(counter);
        }
    }

    private void TryRaiseBuff(RoomHazardIndex.BuffCounter counter)
    {
        // A passive immunity guard (the desert sunstone wristband) makes the whole
        // hazard a no-op just by being POSSESSED — carried or worn, no `use` needed
        // (user-confirmed: the player only has to have it). Spending a waterskin
        // charge to raise the buff is pointless while one is held, so skip the `use`
        // entirely (report -112011: a swig fired in the Scorching Desert though the
        // sunstone already granted immunity). `_carriedCount` counts pack + worn.
        if (FirstCarried(counter.ImmunityItems) is int guard and > 0)
        {
            _log?.Info(LogCategory,
                $"buff spell {counter.BuffSpell}: immune via {_itemName(guard) ?? guard.ToString()} — no `use` needed");
            return;
        }

        int pick = FirstCarried(counter.SourceItems);
        if (pick == 0)
        {
            // Nothing carried to raise this buff. The route's own gating / obtain
            // detour owns getting one; here we can only note the exposure.
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: no source item carried — can't raise");
            return;
        }

        int refreshSec = counter.DurationSeconds > 0
            ? Math.Max(1, counter.DurationSeconds - RefreshMarginSeconds)
            : UnknownDurationRefreshSeconds;
        DateTimeOffset now = _now();
        if (_lastUsed.TryGetValue(pick, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(refreshSec))
            return;   // buff still up — don't spend a charge

        if (SendUse(pick, now) is not { } name)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: item {pick} has no name — can't `use`");
            return;
        }
        _log?.Info(LogCategory,
            $"raised buff {counter.BuffSpell} with `use {name}` (refresh ~{refreshSec}s)");
    }

    // Arm the reactive layer for this hazard's buff: compile the lapse-prompt and
    // swig-confirmation line predicates so a lapse fired mid-room (not on a step)
    // is still caught. Re-arming the SAME buff is a no-op so an in-flight
    // _awaitingSwig latch survives a re-approach of the room we're already in.
    private void Arm(RoomHazardIndex.BuffCounter counter)
    {
        if (_activeCounter is { } cur && cur.BuffSpell == counter.BuffSpell) return;
        _activeCounter = counter;
        _lapseMatch = counter.LapseSpell > 0
            ? _messageMatcherForSpell?.Invoke(counter.LapseSpell)
            : null;
        _swigMatch = _messageMatcherForSpell?.Invoke(counter.BuffSpell);
        _awaitingSwig = false;
    }

    // Fed every server line during a walk. Recognises the armed hazard's swig
    // confirmation (clears the latch — the `use` drew a charge) and its lapse
    // prompt (re-raises once, or halts when out of charges).
    public void OnServerLine(string line)
    {
        if (_activeCounter is not { } counter) return;
        if (_swigMatch?.Invoke(line) == true) { _awaitingSwig = false; return; }
        if (_lapseMatch?.Invoke(line) == true) HandleThirst(counter);
    }

    private void HandleThirst(RoomHazardIndex.BuffCounter counter)
    {
        // Only ever act on a live walk — ours, or the leader's we're following. A
        // lapse line seen while idle is just the player standing in the room, not a
        // route committing to march through.
        if (!_walkActive() && !_followingLeader()) return;

        // Immune via a passive guard — the lapse prompt can't actually harm us, so
        // neither re-`use` (pointless) nor halt (we're safe). Just walk on.
        if (FirstCarried(counter.ImmunityItems) is int guard and > 0)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: lapse prompt ignored — immune via {_itemName(guard) ?? guard.ToString()}");
            return;
        }

        // A prior `use` never produced its swig: the item drew nothing (out of
        // charges / waterskins). Re-firing would march deeper into a hazard we can
        // no longer counter, so halt instead of firing into the void.
        if (_awaitingSwig)
        {
            AnnounceOut(counter);
            Halt($"buff {counter.BuffSpell}: lapse prompt with no swig — out of charges");
            return;
        }

        int pick = FirstCarried(counter.SourceItems);
        if (pick == 0)
        {
            AnnounceOut(counter);
            Halt($"buff {counter.BuffSpell}: lapsed with no source item carried");
            return;
        }

        if (SendUse(pick, _now()) is not { } name)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: item {pick} has no name — can't re-raise");
            return;
        }
        _log?.Info(LogCategory,
            $"re-raised buff {counter.BuffSpell} with `use {name}` on lapse prompt");
    }

    // `use` the carried source item, latch _awaitingSwig for the swig confirmation,
    // and stamp the refresh timer. Returns the item name used, or null when the
    // item resolves to no name (nothing sent).
    private string? SendUse(int itemId, DateTimeOffset now)
    {
        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return null;
        _wire.Send($"use {name}");
        _lastUsed[itemId] = now;
        _awaitingSwig = true;
        _announcedOut = false;
        return name;
    }

    // Tell the room we can no longer counter the hazard — out of the buff's source
    // item — so the party knows why we're taking the damage (a follower can't stop
    // the leader's route itself). Said once per run-out; the `.` precursor makes it
    // a plain say whatever the line starts with.
    private void AnnounceOut(RoomHazardIndex.BuffCounter counter)
    {
        if (_announcedOut) return;
        _announcedOut = true;
        string item = counter.SourceItems.Count > 0 && _itemName(counter.SourceItems[0]) is { Length: > 0 } n
            ? n : "water";
        string plural = item.EndsWith('s') ? item : item + "s";
        _wire.Send($".I'm out of {plural}!");
        _log?.Info(LogCategory, $"out of {plural} — told the room");
    }

    // Out of ways to raise the buff. Our own walk backs out rather than marching
    // deeper; a follower has no walk to halt — the leader owns the route — so it only
    // records it.
    private void Halt(string why)
    {
        if (_walkActive())
        {
            _log?.Info(LogCategory, $"halting walk — {why}");
            _haltWalk?.Invoke(why);
        }
        else
        {
            _log?.Info(LogCategory, $"following through the hazard unprotected — {why}");
        }
        Disarm();
    }

    private void Disarm()
    {
        _activeCounter = null;
        _lapseMatch = null;
        _swigMatch = null;
        _awaitingSwig = false;
    }

    private int FirstCarried(IReadOnlyList<int> items)
    {
        foreach (int id in items)
            if (_carriedCount(id) > 0) return id;
        return 0;
    }
}
