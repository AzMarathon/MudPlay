using MudPlay.Services;

namespace MudPlay.Game.Map;

// Keeps a checkspell hazard buff up while the character is in hazard country.
//
// Some room hazards (the Scorching Desert's heat) are survived not by HOLDING a
// counter but by an active buff the player raises with `use <item>`: the desert
// waterskin `use`s to cast buff 711, safe only while that buff is up. Carrying the
// source item is enough to let the route pass the hazard gate
// (RoomHazardIndex.RoomHazard.IsSatisfiedBy checks carrying), but the buff must
// actually be RAISED, or the route walks straight into the damage — the "it required
// a waterskin but never used it" report.
//
// The buff is tracked quietly (user, 2026-10-10): when it went on, how long it
// lasts by the game data, and so when it runs out. It is raised again shortly before
// that and not sooner, since every `use` spends a charge, and only while it matters:
// standing in a room the buff counters, or with one a few planned steps ahead.
//
//  • On since — a `use` this engine sends stamps it at the send, and the buff
//    spell's own line (the swig) stamps it again when it shows, for a `use` typed by
//    hand as much as for ours. The waterskin buff has no wear-off line, so the end
//    is worked out from the duration. A death, a dropped link, a new profile and a
//    new game-data set forget it: unknown counts as off when a hazard room is next.
//  • The window — with Auto-Sneak on, inside RefreshWindowSeconds of the end, and
//    with a countered room here or a few planned steps ahead, the refresh is offered
//    to the between-round cast scheduler (CastingDirector.SetClientUseSource), which
//    gives it the round's one cast and holds it for sneak keeping like any buff: it
//    waits for a room with no NPCs, goes out there, and the re-sneak that follows is
//    answered before the next step. The early window and the look ahead exist to buy
//    that search and nothing else, so with Auto-Sneak off there is neither: "not too
//    soon before it wears off or we'll be using a lot more waterskins than we should
//    be" (user, 2026-10-10). The refresh then keeps to the last call.
//  • The last call — inside ForcedLeadSeconds of the end (or with the buff off)
//    it can't wait any longer. The step into a countered room sends it where the
//    character stands (OnApproachingRoom, asked ahead of the sneak check), a
//    follower's arrival does (OnArrivedInRoom), and the scheduler is told it must
//    go now. A sneak ended that way is reported (SneakSpentHere) so the reactions
//    to a lost sneak apply as for any other.
//  • Reactive (message) — the timer only estimates the lapse. When the buff drops
//    early the room casts its lapse-damage spell and the game prints that spell's
//    message ("you suffer in the desert heat... you need water, soon!"). Seeing it,
//    we fire exactly ONE `use` to re-raise. The trigger is the game-data message
//    record linked to the hazard's lapse spell (RoomHazardIndex.BuffCounter
//    .LapseSpell), not hardcoded realm text. If a SECOND lapse prompt arrives before
//    the swig, the `use` drew nothing (out of charges / waterskins) and we HALT the
//    walk rather than march deeper into a hazard we can no longer counter.
//
// A party FOLLOWER runs no walk of its own — the leader's route carries it — so it
// raises the buff on arriving in a hazard room, and the window and the lapse prompt
// work for it as on a walk.
//
// No toggle of its own: surviving a hazard room the route already commits to
// walking is not opt-in (mirrors auto-light's "leave it off if you don't want it" —
// here the equivalent is simply not routing through the hazard). The master switch
// is the one thing that stops it, as a survival item too: "with the master switch
// off, it shouldnt automatically swap gear" (user, 2026-10-10). Off, no `use`, no
// re-raise and no "out of" say goes out, and no latch or timer is stamped, so
// when the switch comes back on the next arrival or lapse acts afresh. It only
// ever acts when a checkspell hazard and a carried source item coincide during a
// live walk — ours, or the leader's we're following.
public sealed class AutoHazardCounterProvisioner
{
    // True (and counted) while the master switch is off; asked right before
    // anything is sent. Null in tests that don't exercise the switch.
    public Func<bool>? MasterSwitchOff { get; set; }

    // LogService category — [HazardCounter] rows per decision.
    public const string LogCategory = "HazardCounter";

    // With Auto-Sneak on the refresh is on offer this long before the buff runs out.
    // One charge buys 1800 s of waterskin, so a refresh at the very start of the
    // window gives up a thirtieth of one; twelve 5 s combat rounds is room to finish
    // a fight, find a room with no NPCs on a sneaked walk and get the round's one
    // cast. With Auto-Sneak off there is no search to buy, and no window.
    public const int RefreshWindowSeconds = 60;

    // Inside this of the end the `use` goes out wherever the character stands: three
    // rounds, one for a cast already made this round, one for the `use`, one for a
    // retry the game turned away. A step into a countered room never lands in the gap
    // between the lapse and the refresh.
    public const int ForcedLeadSeconds = 15;

    // With Auto-Sneak on, how many planned steps ahead a countered room makes the buff
    // matter. Far enough to find a room with no NPCs before the edge, near enough
    // that a route passing by spends nothing. With Auto-Sneak off nothing is looked
    // ahead for: the first drink goes out at the edge, on the step's last call.
    public const int LookaheadSteps = 5;

    // Fallback refresh interval when the buff's duration isn't in the data (Dur 0):
    // still re-use periodically rather than once-and-never so an un-timed counter
    // doesn't silently lapse.
    private const int UnknownDurationRefreshSeconds = 60;

    // A refusal line is taken as the answer to our `use` only this soon after it.
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(5);

    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int, RoomHazardIndex.RoomHazard?> _hazardForSpell;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    // Resolve a spell number to a predicate that recognises that spell's own
    // game-data message on a server line (lapse prompt for LapseSpell, swig
    // confirmation for BuffSpell). Null → the reactive path stays inert and only
    // the timer keeps the buff up.
    private readonly Func<int, Func<string, bool>?>? _messageMatcherForSpell;
    private readonly Func<bool> _walkActive;
    private readonly Func<bool> _followingLeader;
    private readonly Action<string>? _haltWalk;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // The room we stand in, then the rooms the walk or loop plans to enter next (at
    // most LookaheadSteps of them). Null → the window is never offered and only the
    // step into a countered room, an arrival and the lapse prompt act.
    private readonly Func<IReadOnlyList<RoomKey>>? _roomsAhead;
    // Every buff counter the active game-data set knows, so the buff's line is read
    // wherever it shows. Null → only the counter last approached is watched.
    private readonly Func<IEnumerable<RoomHazardIndex.BuffCounter>>? _allCounters;
    // SneakGuard.Holds: a sneak is being kept that a `use` would end.
    private readonly Func<bool>? _sneakKept;
    // Auto-Sneak is on: the refresh gets its window and its look ahead, to find a
    // room with no NPCs in. Null → off.
    private readonly Func<bool>? _autoSneakOn;
    // Charges left in the carried item, null when the client doesn't know.
    private readonly Func<int, int?>? _chargesLeft;
    // The engine send gate is up (a password prompt, mortally wounded): a send now
    // would be dropped, and must not be stamped as made.
    private readonly Func<bool>? _sendBlocked;

    // Per buff spell: when it went on, and whether its own line said so or we only
    // know the `use` went out.
    private readonly Dictionary<int, (DateTimeOffset OnSince, bool Confirmed)> _on = new();

    // Our last `use`, until its answer: what the buff's tracking was before it, to
    // put back if the game turns the `use` away.
    private (int BuffSpell, int Item, DateTimeOffset SentAt, (DateTimeOffset OnSince, bool Confirmed)? Before)? _pending;

    // Per source item: the game refused a `use` of it for a reason that holds (not
    // carried, no uses left, not usable), with the count carried at the time. Nothing
    // more is sent for the item until that count changes or the buff's line shows.
    private readonly Dictionary<int, int> _refusedAtCount = new();

    // Buff-line watchers for every known counter, built on first use and dropped by
    // Forget so a new game-data set rebuilds them.
    private List<(int BuffSpell, Func<string, bool> Match)>? _buffLines;

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

    // Said once per refresh: that it is due, and that it is waiting on the sneak.
    private enum DueSaid { Nothing, Due, CannotWait }
    private DueSaid _dueSaid;
    private bool _waitLogged;

    // A `use` went out, by whichever road. The cast scheduler takes it for the
    // round's between-round cast, so the fight resumes and the sneak is taken again.
    public event Action? UseSent;

    // A `use` went out although a sneak was being kept, because it could wait no
    // longer. The sneak is gone in this room, for whatever reacts to that.
    public event Action? SneakSpentHere;

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
        Func<bool>? followingLeader = null,
        Func<IReadOnlyList<RoomKey>>? roomsAhead = null,
        Func<IEnumerable<RoomHazardIndex.BuffCounter>>? allCounters = null,
        Func<bool>? sneakKept = null,
        Func<int, int?>? chargesLeft = null,
        Func<bool>? sendBlocked = null,
        Func<bool>? autoSneakOn = null)
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
        _roomsAhead = roomsAhead;
        _allCounters = allCounters;
        _sneakKept = sneakKept;
        _chargesLeft = chargesLeft;
        _sendBlocked = sendBlocked;
        _autoSneakOn = autoSneakOn;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel. Until bound, a `use` is recorded for tests only.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- the buff's clock ------------------------------------------------

    // How long the buff lasts: the spell's Dur in spell rounds at 3 s each, taken at
    // the spell's own level (RoomHazardIndex.ReadSpellDurations), so the shortest it
    // can be. A buff with no duration in the data is given the fallback interval.
    private static TimeSpan DurationOf(in RoomHazardIndex.BuffCounter counter) =>
        TimeSpan.FromSeconds(counter.DurationSeconds > 0
            ? counter.DurationSeconds
            : UnknownDurationRefreshSeconds + ForcedLeadSeconds);

    // Time left on the buff, null when it isn't known to be on.
    private TimeSpan? RemainingOf(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        _on.TryGetValue(counter.BuffSpell, out (DateTimeOffset OnSince, bool Confirmed) on)
            ? on.OnSince + DurationOf(counter) - now
            : null;

    // Off, unknown or about to run out: the `use` can't wait.
    private bool MustUseNow(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        RemainingOf(counter, now) is not { } left || left <= TimeSpan.FromSeconds(ForcedLeadSeconds);

    // Inside the refresh window. A buff of unknown length has no window before its
    // last call: there is no end to count back from.
    private bool InWindow(in RoomHazardIndex.BuffCounter counter, DateTimeOffset now) =>
        RemainingOf(counter, now) is not { } left
        || left <= TimeSpan.FromSeconds(counter.DurationSeconds > 0 ? RefreshWindowSeconds : ForcedLeadSeconds);

    // Forget what is believed about every buff: a death wipes them, and across a
    // dropped link, a new profile or a new game-data set nothing here can be vouched
    // for. Unknown counts as off when a hazard room is next.
    public void Forget(string why)
    {
        bool known = _on.Count > 0;
        _on.Clear();
        _pending = null;
        _refusedAtCount.Clear();
        _buffLines = null;
        _dueSaid = DueSaid.Nothing;
        _waitLogged = false;
        Disarm();
        if (known) _log?.Info(LogCategory, $"buff tracking reset ({why}) — treated as off until used again");
    }

    // ----- when a `use` goes out --------------------------------------------

    // Predictive last call. The walker / loop-runner ask this as they commit to a
    // step, with the room about to be entered, BEFORE the sneak check and the move
    // bytes. If that room's cast-on-enter spell is a checkspell hazard whose buff is
    // off or about to run out, and we carry its source, we `use` it now — so the buff
    // is up when the step lands. A no-op for a seeable / benign / passive-counter
    // room, and while the buff still has time on it.
    public void OnApproachingRoom(RoomKey target)
    {
        if (_resolveRoom(target) is not { } room) return;
        if (room.Spell <= 0) return;
        if (_hazardForSpell(room.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
        {
            Arm(counter);
            TryRaiseBuff(counter, room);
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
            TryRaiseBuff(counter, r);
        }
    }

    private void TryRaiseBuff(RoomHazardIndex.BuffCounter counter, Room room)
    {
        if (SourceToUse(counter) is not int pick) return;

        DateTimeOffset now = _now();
        if (!MustUseNow(counter, now)) return;   // buff still up — don't spend a charge

        // Asked after the timing check, before SendUse stamps anything, so a skip
        // leaves no timer behind that would read as a buff still covering us.
        if (MasterSwitchOff?.Invoke() == true) return;
        if (OutOfCharges(counter, pick)) return;

        // Read before the send: the `use` itself ends the sneak being kept.
        bool overSneak = _sneakKept?.Invoke() == true;
        if (SendUse(counter, pick, now) is not { } name) return;
        _log?.Info(LogCategory, overSneak
            ? $"forced: `use {name}` for buff {counter.BuffSpell} before {Describe(room)} — it could not wait for a room with no NPCs, and the sneak is spent here"
            : $"used `use {name}` for buff {counter.BuffSpell} before {Describe(room)} ({Lasts(counter)})");
        if (overSneak) SneakSpentHere?.Invoke();
    }

    // The source item to `use` for this counter, or null when nothing is to be sent:
    // a guard makes the hazard a no-op, nothing is carried to raise the buff, the item
    // is out of charges or was refused, or the send gate is up.
    private int? SourceToUse(in RoomHazardIndex.BuffCounter counter)
    {
        // A passive immunity guard (the desert sunstone wristband) makes the whole
        // hazard a no-op just by being POSSESSED — carried or worn, no `use` needed
        // (user-confirmed: the player only has to have it). Spending a waterskin
        // charge to raise the buff is pointless while one is held, so skip the `use`
        // entirely (report -112011: a swig fired in the Scorching Desert though the
        // sunstone already granted immunity). `_carriedCount` counts pack + worn.
        if (FirstCarried(counter.ImmunityItems) is int guard and > 0)
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: immune via {_itemName(guard) ?? guard.ToString()} — no `use` needed");
            return null;
        }

        int pick = FirstCarried(counter.SourceItems);
        if (pick == 0)
        {
            // Nothing carried to raise this buff. The route's own gating / obtain
            // detour owns getting one; here we can only note the exposure.
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: no source item carried — can't raise");
            return null;
        }

        if (_refusedAtCount.TryGetValue(pick, out int countThen))
        {
            if (_carriedCount(pick) == countThen) return null;   // said when it was refused
            _refusedAtCount.Remove(pick);
        }
        if (_sendBlocked?.Invoke() == true) return null;
        return pick;
    }

    // True when the client's charge count for the item reads none left: nothing is
    // sent, and the item is set aside until the pack changes. Asked only when a `use`
    // is about to go out, since the count is read from the item table.
    private bool OutOfCharges(in RoomHazardIndex.BuffCounter counter, int pick)
    {
        if (_chargesLeft?.Invoke(pick) != 0) return false;
        _refusedAtCount[pick] = _carriedCount(pick);
        _log?.Info(LogCategory,
            $"refused: {_itemName(pick) ?? pick.ToString()} has no charges left for buff {counter.BuffSpell} — not sent until the pack changes");
        return true;
    }

    // What the cast scheduler is offered: the command to send, and whether it can
    // wait for a better moment. Null when nothing is due — the buff has time on it,
    // no countered room is here or a few steps ahead, or nothing can raise it.
    public (string What, bool MustGoNow)? DueNow()
    {
        if (DueUse(out _, out _, out bool mustGoNow, out TimeSpan? left, out Room? where) is not { } name)
        {
            _dueSaid = DueSaid.Nothing;
            _waitLogged = false;
            return null;
        }
        string what = $"use {name}";
        DueSaid saying = mustGoNow ? DueSaid.CannotWait : DueSaid.Due;
        if (_dueSaid != saying)
        {
            _dueSaid = saying;
            _log?.Info(LogCategory, $"due: `{what}` — the buff "
                + (left is { } l ? $"runs out in {Math.Max(0, (int)l.TotalSeconds)} s" : "is off or not known to be on")
                + (where is null ? "" : $", and {Describe(where)} needs it")
                + (mustGoNow ? "; it can't wait" : ""));
        }
        return (what, mustGoNow);
    }

    // The scheduler held the due `use` to keep a sneak: it waits for a room with no
    // NPCs, or for the last call.
    public void NoteHeldForSneak()
    {
        if (_waitLogged) return;
        _waitLogged = true;
        _log?.Info(LogCategory, "waiting for a room with no NPCs before the `use`, so the sneak isn't spent beside one");
    }

    // The scheduler has the round's cast for us: send the due `use`. sneakKept says a
    // sneak was being kept that this ends. False when nothing was sent.
    public bool FireDue(bool sneakKept)
    {
        if (DueUse(out RoomHazardIndex.BuffCounter counter, out int pick, out bool mustGoNow, out _, out Room? where) is null)
            return false;
        if (MasterSwitchOff?.Invoke() == true) return false;
        if (OutOfCharges(counter, pick)) return false;
        Arm(counter);
        if (SendUse(counter, pick, _now()) is not { } name) return false;
        string place = where is null ? "" : $" {Describe(where)}";
        _log?.Info(LogCategory, sneakKept
            ? $"forced: `use {name}` for buff {counter.BuffSpell}{place} — no room with no NPCs turned up in time, and the sneak is spent here"
            : $"used `use {name}` for buff {counter.BuffSpell}{place}{(mustGoNow ? ", at the last call" : "")} ({Lasts(counter)})");
        if (sneakKept) SneakSpentHere?.Invoke();
        return true;
    }

    // The due `use`, if any: the item's name, with the counter, the item, whether it
    // can wait, the time left on the buff and the countered room that makes it matter.
    private string? DueUse(
        out RoomHazardIndex.BuffCounter counter, out int pick, out bool mustGoNow, out TimeSpan? left, out Room? where)
    {
        counter = default;
        pick = 0;
        mustGoNow = false;
        left = null;
        where = null;
        // Only on a live walk, ours or the leader's: standing in a hazard room with
        // nothing running is the player's own business.
        if (!_walkActive() && !_followingLeader()) return null;
        if (_roomsAhead?.Invoke() is not { Count: > 0 } rooms) return null;

        // The window and the look ahead buy the search for a room with no NPCs. With
        // Auto-Sneak off there is nothing to search for: only the room we stand in
        // counts, and only at the last call.
        bool searching = _autoSneakOn?.Invoke() == true;
        int reach = searching ? rooms.Count : 1;
        DateTimeOffset now = _now();
        for (int i = 0; i < reach; i++)
        {
            if (_resolveRoom(rooms[i]) is not { Spell: > 0 } room) continue;
            if (_hazardForSpell(room.Spell) is not { } hazard) continue;
            foreach (RoomHazardIndex.BuffCounter c in hazard.BuffCounters)
            {
                if (!(searching ? InWindow(c, now) : MustUseNow(c, now))) continue;
                if (SourceToUse(c) is not int item) continue;
                if (_itemName(item) is not { Length: > 0 } name) continue;
                counter = c;
                pick = item;
                left = RemainingOf(c, now);
                where = room;
                // Standing in the countered room there is nothing to wait for. One
                // ahead is the step's own last call (OnApproachingRoom).
                mustGoNow = i == 0 && MustUseNow(c, now);
                return name;
            }
        }
        return null;
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

    // ----- what the game says ------------------------------------------------

    // Fed every server line. Reads a buff's own line wherever it shows (it is on,
    // from now), the answer to a `use` of ours the game turned away, and the armed
    // hazard's lapse prompt (re-raises once, or halts when out of charges).
    public void OnServerLine(string line)
    {
        if (BuffLineOf(line) is int buffSpell)
        {
            _on[buffSpell] = (_now(), true);
            _pending = null;
            _refusedAtCount.Clear();
            if (_activeCounter is { } armed && armed.BuffSpell == buffSpell) _awaitingSwig = false;
            return;
        }
        if (_pending is { } sent && TakeRefusal(line, sent)) return;
        if (_activeCounter is { } counter && _lapseMatch?.Invoke(line) == true) HandleThirst(counter);
    }

    // The buff whose own line this is, or null.
    private int? BuffLineOf(string line)
    {
        if (_activeCounter is { } armed && _swigMatch?.Invoke(line) == true) return armed.BuffSpell;
        if (_allCounters is null || _messageMatcherForSpell is null) return null;
        if (_buffLines is null)
        {
            _buffLines = new();
            foreach (RoomHazardIndex.BuffCounter c in _allCounters())
                if (_buffLines.All(b => b.BuffSpell != c.BuffSpell) && _messageMatcherForSpell(c.BuffSpell) is { } match)
                    _buffLines.Add((c.BuffSpell, match));
        }
        foreach ((int spell, Func<string, bool> match) in _buffLines)
            if (match(line)) return spell;
        return null;
    }

    // The Stock engine's answers to a `use` it won't carry out (GAME_MECHANICS
    // "Equip → use → restore swap for a readied buff item"), read only right behind
    // a `use` of ours. The round refusal is tried again a round later; the others
    // hold until the pack changes, since asking again would only draw the same line.
    private bool TakeRefusal(string line, (int BuffSpell, int Item, DateTimeOffset SentAt, (DateTimeOffset OnSince, bool Confirmed)? Before) sent)
    {
        if (_now() - sent.SentAt > AnswerWindow)
        {
            _pending = null;
            return false;
        }
        // The two lines that name the item are ours only when they name this one:
        // "You don't have ..." answers other commands as well.
        string named = (_itemName(sent.Item) ?? string.Empty).Split(' ')[0];
        bool namesIt = named.Length > 0 && line.Contains(named, StringComparison.OrdinalIgnoreCase);
        string? why =
            line.StartsWith("You have already cast a spell this round!", StringComparison.Ordinal) ? "already cast this round — trying again next round"
            : namesIt && line.StartsWith("You don't have ", StringComparison.Ordinal) ? "the game says it isn't carried"
            : namesIt && line.StartsWith("There are no more uses in ", StringComparison.Ordinal) ? "no uses left in it"
            : line.StartsWith("You may not use that item!", StringComparison.Ordinal) ? "the character may not use it"
            : null;
        if (why is null) return false;

        _pending = null;
        _awaitingSwig = false;
        if (sent.Before is { } before) _on[sent.BuffSpell] = before;
        else _on.Remove(sent.BuffSpell);
        bool again = line.StartsWith("You have already cast", StringComparison.Ordinal);
        if (!again) _refusedAtCount[sent.Item] = _carriedCount(sent.Item);
        _log?.Info(LogCategory,
            $"refused: `use {_itemName(sent.Item) ?? sent.Item.ToString()}` for buff {sent.BuffSpell} — {why}"
            + (again ? "" : "; not sent again until the pack changes"));
        return true;
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

        // Before the out-of-charges branches too: they say "I'm out of …" and halt
        // the walk, and neither may happen (nor latch _announcedOut) while off.
        if (MasterSwitchOff?.Invoke() == true) return;

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

        if (SendUse(counter, pick, _now()) is not { } name) return;
        _log?.Info(LogCategory,
            $"re-raised buff {counter.BuffSpell} with `use {name}` on lapse prompt — it ran out sooner than its timer said");
    }

    // `use` the carried source item: stamp the buff as on from now, remember what it
    // was before in case the game turns the `use` away, and latch _awaitingSwig for
    // the swig confirmation. Returns the item name used, or null when the item
    // resolves to no name (nothing sent).
    private string? SendUse(in RoomHazardIndex.BuffCounter counter, int itemId, DateTimeOffset now)
    {
        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name))
        {
            _log?.Debug(LogCategory, $"buff spell {counter.BuffSpell}: item {itemId} has no name — can't `use`");
            return null;
        }
        _wire.Send($"use {name}");
        _pending = (counter.BuffSpell, itemId, now,
            _on.TryGetValue(counter.BuffSpell, out (DateTimeOffset OnSince, bool Confirmed) before) ? before : null);
        _on[counter.BuffSpell] = (now, false);
        _awaitingSwig = true;
        _announcedOut = false;
        _dueSaid = DueSaid.Nothing;
        _waitLogged = false;
        UseSent?.Invoke();
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

    // ----- for the log and the bug report ------------------------------------

    private static string Describe(Room room) => $"{room.Key} ({room.Name})";

    private string Lasts(in RoomHazardIndex.BuffCounter counter) => counter.DurationSeconds > 0
        ? $"lasts {counter.DurationSeconds} s; {Refreshed()}"
        : $"length not in the game data; used again after {UnknownDurationRefreshSeconds} s";

    // When the refresh comes, as things stand.
    private string Refreshed() => _autoSneakOn?.Invoke() == true
        ? $"refresh window {RefreshWindowSeconds} s, last call {ForcedLeadSeconds} s"
        : $"refreshed in its last {ForcedLeadSeconds} s (Auto-Sneak off: no window, no look ahead)";

    // The tracked buffs, one line each, for the bug report. Empty when none is known
    // to be on.
    public IReadOnlyList<string> DescribeTracking()
    {
        DateTimeOffset now = _now();
        List<string> lines = new();
        foreach ((int buffSpell, (DateTimeOffset onSince, bool confirmed)) in _on)
        {
            RoomHazardIndex.BuffCounter? counter = CounterOf(buffSpell);
            string ends = counter is { } c
                ? $"runs out {(onSince + DurationOf(c)).ToLocalTime():HH:mm:ss} (in {(int)(onSince + DurationOf(c) - now).TotalSeconds} s)"
                : "length unknown";
            lines.Add($"buff {buffSpell}: on since {onSince.ToLocalTime():HH:mm:ss} "
                + (confirmed ? "(its line was seen)" : "(a `use` went out; its line not seen)")
                + $", {ends}; {Refreshed()}"
                + (_pending is { } p && p.BuffSpell == buffSpell ? "; answer to the `use` still awaited" : ""));
        }
        foreach ((int item, int count) in _refusedAtCount)
            lines.Add($"{_itemName(item) ?? $"item {item}"}: a `use` was refused or it has no charges; nothing sent while {count} are carried");
        if (_dueSaid != DueSaid.Nothing)
            lines.Add(_dueSaid == DueSaid.CannotWait ? "a `use` is due and can't wait"
                : _waitLogged ? "a `use` is due, waiting for a room with no NPCs"
                : "a `use` is due, waiting for the round's cast");
        return lines;
    }

    private RoomHazardIndex.BuffCounter? CounterOf(int buffSpell)
    {
        if (_activeCounter is { } armed && armed.BuffSpell == buffSpell) return armed;
        if (_allCounters is null) return null;
        foreach (RoomHazardIndex.BuffCounter c in _allCounters())
            if (c.BuffSpell == buffSpell) return c;
        return null;
    }
}
