using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Puts on the item that negates a room's own spell before the character steps into
// the room, and gives the slot back afterwards (user, 2026-10-10: "phoenix feather
// should be worn, or magma amulet if no phoenix feather, before stepping into magma
// drips that are prevented by it. and put the default piece(or whatever set is
// equipped) back on once we're no longer about to enter a room spell that needs the
// feather"). A negating item works only while it is worn, on both realms
// (GAME_MECHANICS "Room-spell hazard shape 1 — direct damage, negated by an item's
// `NegateSpell-N`"), so one carried in the pack counters nothing.
//
// The wear and the restore are EquipmentManager's slot claims, the mechanism a
// location rule uses: while the slot is claimed the gear sets leave it alone, and
// giving it back puts on whatever the set in force has for it. So another swap in
// the meantime doesn't take the counter off, and a set changed while it was on is
// the one honoured on the way out. The counter's claim is the pressing one: a
// location rule holding the same slot with another piece gives way to it, and has
// its piece back when the counter lets go.
//
// Where it is on (user, 2026-10-10: "the counter should stay on while inside the
// room that it is needed, and be put on when we're next to a room where it is
// needed. if we exit a room where its needed but are within 1 room in any
// direction of the hazard, it should stay on."): in a room whose spell it negates,
// and in any room with an exit into one. It goes on when the character arrives
// next to such a room, so it is worn before the step in, and comes off when
// neither holds. The rule is the same whether or not something is moving the
// character, and it reads only the exits of the room stood in.
//
// The wear before a step (the engine's ready check, which holds the step until the
// game answers and no longer than AnswerCap) and the wear ahead of a typed step
// are what is left for an arrival that had no room next to it first: a teleport, a
// leader's drag, an exit the map can't follow. Standing next to or in such a room
// it also goes on the moment the item turns up in the pack, the pack is first
// read, or the setting is switched on (Recheck).
//
// Sneaking past things (user, 2026-10-10: "it should try to wear the counter in an
// empty room is auto-sneak is on and auto-combat is off, if auto sneak is on and
// auto-combat is on, wear it before the hazard rooms where appropriate."): a gear
// command ends a sneak, and with Auto-Sneak on and Auto-Combat off the character is
// passing NPCs it won't fight. So in that case alone the plan is read
// EmptyRoomLookAhead steps on, and the counter goes on in a room with no NPC in it
// when such a room comes up before the hazard; the `sn` before the next step takes
// the sneak again. With no empty room on the way it goes on next to the hazard as
// above, sneak or not. That ruling is about the wear, which has the room's damage
// at stake. Giving the slot back has nothing at stake, so it never goes out
// through a sneak the guard is keeping, in any combination of the two settings: it
// waits (in the sneaking-past case, for a room with no NPC), the item stays owned,
// and it is decided again when the wait is over.
//
// One step of the plan keeps it on (user, 2026-10-10: "stay on while the next
// planned step is again next to the hazard"): where it would come off, it stays
// when the moving engine's next planned step lands in or next to a room it is for,
// so a corridor with such a room off every second room is walked with one wear
// and one restore. The look is one step deep and read off the plan: with the room
// off every third room it still swaps, and with no plan (typed moves, a follower
// being led) the rule is the plain one above.
//
// What is on is this class's own knowledge until the inventory agrees. The game's
// wear line reaches it before the inventory's copy of the worn list is brought up
// to date, and an engine resumed by that line asks again at once: answered from
// the inventory alone, it sent the wear a second time (the second one's answer
// then stalled the step and lost the displaced piece).
public sealed class RoomSpellCounterWear
{
    // Shares the Equipment category, like the location rules: the wear and the
    // revert are logged there by EquipmentManager, and these lines say why.
    public const string LogCategory = EquipmentManager.LogCategory;
    public const string AsserterName = nameof(RoomSpellCounterWear);

    // The name the slot is claimed under, as it reads in EquipmentManager's log lines.
    public const string Owner = "room-spell counter";

    // How long a step waits for the game to answer the wear: the sneak-first hold's
    // own bound, so a line that never comes can't strand a walk. One step is held
    // for one unanswered wear at most; a second candidate tried for the same step
    // goes out ahead of it with no hold.
    public static readonly TimeSpan AnswerCap = TimeSpan.FromSeconds(3);

    // A wear that drew no answer in AnswerCap is listened for this much longer, so
    // an answer delayed by lag still finds the item owned and its slot given back
    // later. After it the slot goes back to the gear sets.
    public static readonly TimeSpan LateAnswerWindow = TimeSpan.FromSeconds(3);

    // How far along the engine's plan an empty room is looked for when sneaking
    // past things: far enough to find one on most approaches, near enough that the
    // counter isn't worn for long before it is needed.
    public const int EmptyRoomLookAhead = 3;

    private readonly EquipmentManager _equipment;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _enabled;
    private readonly Func<RoomKey, Room?> _roomOf;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<int, IReadOnlyList<int>> _negatorsOf;
    private readonly Func<int, RoomSpellCounterItem?> _describe;
    private readonly Func<int, bool> _isWorn;
    private readonly Func<int, bool> _isCarried;
    private readonly Func<EquipmentSlot, IReadOnlyList<RoomSpellCounterItem>> _wornIn;
    private readonly Func<int, string?> _spellName;
    private readonly Func<bool> _sneakingPast;
    private readonly Func<bool> _roomEmpty;
    private readonly Func<int, IReadOnlyList<RoomKey>> _plannedAhead;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action? _gearCommandSent;
    private readonly LogService? _log;

    // What this class put on and still answers for: the item, the piece the game
    // took out of its place (none when a free place was filled), and the spell it
    // went on for. Kept until the commands that give the slot back have gone out.
    private readonly record struct Owned(string Name, string? Displaced, int Spell);
    private readonly Dictionary<int, Owned> _owned = new();

    // Items the game said are on, until the inventory's worn list says so too.
    private readonly HashSet<int> _confirmedOn = new();

    // The wear whose answer hasn't come yet: the pieces that were in its slot when
    // it went out, and the one the game has since said it removed.
    private sealed record Wear(int Id, string Name, int Spell, IReadOnlyList<string> InSlotBefore)
    {
        public string? Evicted { get; set; }
    }
    private Wear? _pending;
    private bool _holding;
    private int _pendingToken;

    // Wears past AnswerCap and still inside LateAnswerWindow, by item.
    private readonly Dictionary<int, Wear> _late = new();

    // One unanswered wear has held this step already: no further hold until the
    // character stands somewhere else.
    private bool _stepHeldOut;

    // Items the game won't let this character wear (`You may not wear that item!`,
    // `<item> may not be worn!`). Class, level and alignment decide that, so they
    // are tried again after a level-up, an alignment change or a stat-screen read.
    private readonly Dictionary<int, SlotWearAnswer> _refused = new();

    // Items whose wear came to nothing for a reason that is about the moment: no
    // answer, no room, not in the pack, a piece in the way that can't come off, or
    // not in the pack as the client reads it. Out of use, and out of route
    // planning, until the inventory is next read in full; the two that a freed slot
    // cures come back when a piece comes off. Null = no answer.
    private readonly Dictionary<int, SlotWearAnswer?> _shelved = new();

    // Something else took a counter of ours off in the room stood in: no counter
    // is put on here again. A step into a room that needs one still gets its wear.
    private bool _takenOffHere;

    // Said once each, not on every look: a carried counter the character can't
    // wear, a wear the locked send gate dropped, a restore a kept sneak is holding.
    private readonly HashSet<int> _saidUnwearable = new();
    private readonly HashSet<int> _saidNotSent = new();
    private readonly HashSet<int> _saidRestoreHeld = new();

    // The master switch (true = off): off, nothing is put on and nothing is given
    // back. What is worn stays as it is until the switch is on again (Recheck is
    // run then). Asking counts a skipped action, so the route planner, which asks
    // for every exit it weighs, reads MasterSwitchIsOff.
    public Func<bool>? MasterSwitchOff { get; set; }
    public Func<bool>? MasterSwitchIsOff { get; set; }

    // Raised when what WillWear answers may have changed, so a caller that keeps
    // the answers (the route planner's memo) can drop them.
    public event Action? UsabilityChanged;

    public RoomSpellCounterWear(
        EquipmentManager equipment,
        MovementCoordinator coordinator,
        Func<bool> enabled,
        Func<RoomKey, Room?> roomOf,
        Func<RoomKey?> currentRoom,
        Func<int, IReadOnlyList<int>> negatorsOf,
        Func<int, RoomSpellCounterItem?> describe,
        Func<int, bool> isWorn,
        Func<int, bool> isCarried,
        Func<EquipmentSlot, IReadOnlyList<RoomSpellCounterItem>> wornIn,
        Func<int, string?> spellName,
        // Auto-Sneak on with Auto-Combat off; no NPC in the room stood in; the
        // rooms the moving engine's plan enters next, nearest first.
        Func<bool> sneakingPast,
        Func<bool> roomEmpty,
        Func<int, IReadOnlyList<RoomKey>> plannedAhead,
        Action<TimeSpan, Action> schedule,
        Action? gearCommandSent = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(roomOf);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(negatorsOf);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(isWorn);
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(wornIn);
        ArgumentNullException.ThrowIfNull(spellName);
        ArgumentNullException.ThrowIfNull(sneakingPast);
        ArgumentNullException.ThrowIfNull(roomEmpty);
        ArgumentNullException.ThrowIfNull(plannedAhead);
        ArgumentNullException.ThrowIfNull(schedule);
        _equipment = equipment;
        _coordinator = coordinator;
        _enabled = enabled;
        _roomOf = roomOf;
        _currentRoom = currentRoom;
        _negatorsOf = negatorsOf;
        _describe = describe;
        _isWorn = isWorn;
        _isCarried = isCarried;
        _wornIn = wornIn;
        _spellName = spellName;
        _sneakingPast = sneakingPast;
        _roomEmpty = roomEmpty;
        _plannedAhead = plannedAhead;
        _schedule = schedule;
        _gearCommandSent = gearCommandSent;
        _log = log;
        _equipment.SlotWearAnswered += OnWearAnswered;
    }

    // What this class has on, for the bug report: the item, what it displaced and
    // the spell it went on for.
    public IReadOnlyList<(string Item, string? Displaced, int Spell)> OwnedSnapshot() =>
        _owned.Values.Select(static o => (o.Name, o.Displaced, o.Spell)).ToList();

    // The items out of use and why, for the bug report.
    public IReadOnlyList<(int Item, string Why)> OutOfUseSnapshot() =>
        _refused.Select(static kv => (kv.Key, $"refused ({kv.Value}); tried again after a level-up, an alignment change or a stat-screen read"))
            .Concat(_shelved.Select(static kv => (kv.Key,
                $"{(kv.Value is { } answer ? answer.ToString() : "no answer to the wear")}; tried again after the next full inventory read")))
            .ToList();

    // The wear still waiting for the game's answer, null when none is.
    public string? PendingItem => _pending?.Name;

    // True when the client is switched on to wear this carried item for a room: the
    // test a route is planned on, so the planner and the wear agree. An item the
    // game refused, one whose wear came to nothing, or one the character can't
    // wear, opens no room.
    public bool WillWear(int itemId) =>
        _enabled() && (MasterSwitchIsOff ?? MasterSwitchOff)?.Invoke() != true
        && !_refused.ContainsKey(itemId) && !_shelved.ContainsKey(itemId)
        && _describe(itemId) is { CanWear: true };

    // Asked by the walker and the loop before a planned step goes out, with the
    // room the step leads into. False holds the step: the wear has gone out and the
    // game hasn't answered it. The engine is driven on again when the hold clears.
    public bool ReadyToEnter(RoomKey? next)
    {
        Room? room = next is { } key ? _roomOf(key) : null;
        if (_pending is { } waiting)
        {
            // A wear already out (sent on arriving next to the room) is waited for
            // like one sent here, when the step is into a room it is for. A step
            // that only passes such a room by goes on.
            if (_stepHeldOut || room is not { Spell: > 0 } || !_negatorsOf(room.Spell).Contains(waiting.Id))
                return true;
            Hold(waiting.Name);
            return false;
        }
        if (room is null) return true;
        return !(TryWear(room, hold: !_stepHeldOut, "before entering") && _holding);
    }

    // A typed step is already on its way out: the wear goes ahead of it on the
    // wire, as a typed step's `sn` does, and nothing is held.
    public void BeforeTypedStep(RoomKey next)
    {
        if (_roomOf(next) is { } room) TryWear(room, hold: false, "ahead of the typed step into");
    }

    // The character stands in a new room: the counter for it, or for a room next to
    // it, goes on, and one needed by neither is given back. A move that landed in
    // such a room with none next to it on the way (a leader's drag, a flee step, a
    // teleport) is countered here, one cast late at worst.
    public void OnArrived(RoomKey room)
    {
        // The tracker confirms the same room more than once (a look, a prediction
        // borne out): only another room is another step.
        if (_lastArrived is not { } last || !last.Equals(room))
        {
            _lastArrived = room;
            _stepHeldOut = false;
            _saidNotSent.Clear();
            _takenOffHere = false;
        }
        Settle(room, "on arriving");
    }

    private RoomKey? _lastArrived;

    // Something changed that may put a counter in reach or make one wanted where
    // the character already stands: the pack, the setting, the master switch, a
    // kept sneak let go, the send gate unlocked, a walk begun or ended (its plan
    // is what an empty room is looked for along). Decided again for the room
    // stood in; a slot waiting to be given back is decided again too, and stays
    // held if the item is needed after all.
    public void Recheck()
    {
        if (_currentRoom() is { } room) Settle(room, "standing");
    }

    // Run on the heartbeat: a wear or a restore waiting for the room to empty is
    // decided again, and nothing is done when none is waiting.
    public void Poll()
    {
        if (_waitingForEmptyRoom) Recheck();
    }

    // A wear ahead of the hazard, or a slot to give back, is waiting for a room
    // with no NPC in it. For the bug report.
    public bool WaitingForEmptyRoom => _waitingForEmptyRoom;
    private bool _waitingForEmptyRoom;

    private void Settle(RoomKey room, string when)
    {
        if (_roomOf(room) is not { } here) return;
        _waitingForEmptyRoom = false;
        // In such a room or next to one: on now, sneak or not.
        HashSet<int> needed = new();
        foreach (Room near in WithNeighbours(here))
        {
            if (near.Spell <= 0) continue;
            needed.Add(near.Spell);
            if (!_takenOffHere)
                TryWear(near, hold: false, ReferenceEquals(near, here) ? $"{when} in" : $"{when} next to");
        }

        // Sneaking past things: the same rooms a few steps on along the plan are
        // dressed for in a room with nobody in it, and keep the counter on from there.
        bool sneakingPast = _sneakingPast();
        bool empty = !sneakingPast || _roomEmpty();
        if (sneakingPast && _enabled() && (MasterSwitchIsOff ?? MasterSwitchOff)?.Invoke() != true)
        {
            foreach (RoomKey key in _plannedAhead(EmptyRoomLookAhead))
            {
                if (_roomOf(key) is not { } ahead) continue;
                foreach (Room near in WithNeighbours(ahead))
                {
                    if (near.Spell <= 0) continue;
                    needed.Add(near.Spell);
                    if (_takenOffHere) continue;
                    if (empty) TryWear(near, hold: false, "in an empty room, ahead of");
                    else if (CouldWearFor(near.Spell)) _waitingForEmptyRoom = true;
                }
            }
        }

        // A counter that would come off here stays on when the plan's very next
        // step lands in or next to a room it is for: one step deep, read off the
        // plan and never searched for. It keeps a counter on and puts none on.
        HashSet<int> nextStep = new();
        if (_owned.Count > 0)
            foreach (RoomKey key in _plannedAhead(1))
                if (_roomOf(key) is { } next)
                    foreach (Room near in WithNeighbours(next))
                        if (near.Spell > 0) nextStep.Add(near.Spell);
        ReleaseUnneeded(here, needed, nextStep, empty);
    }

    // The room and every room an exit of it leads into.
    private IEnumerable<Room> WithNeighbours(Room room)
    {
        yield return room;
        foreach (RoomExit exit in room.Exits.Values)
            if (!exit.Target.Equals(room.Key) && _roomOf(exit.Target) is { } beyond) yield return beyond;
    }

    private bool CouldWearFor(int spell)
    {
        IReadOnlyList<int> negators = _negatorsOf(spell);
        return negators.Count > 0 && !negators.Any(IsOn)
            && negators.Any(id => _isCarried(id) && !_refused.ContainsKey(id) && !_shelved.ContainsKey(id));
    }

    // The inventory was read in full: its worn list is the truth now, and whatever
    // was set aside on a guess about the pack gets another try.
    public void NoteInventoryRead()
    {
        _confirmedOn.Clear();
        // A wear given up on that the read shows on after all (its answer was lost,
        // not the wear): held from here like any other, so the slot goes back later.
        foreach (Wear wear in _gaveUp.Values)
        {
            if (!_isWorn(wear.Id)
                || _equipment.ClaimSlot(wear.Name, Owner, urgent: true) != SlotClaimResult.AlreadyWorn) continue;
            _owned[wear.Id] = new Owned(wear.Name, wear.Evicted, wear.Spell);
            _log?.Info(LogCategory, $"{Owner}: the inventory shows '{wear.Name}' on — its slot is held until it is no longer needed");
        }
        _gaveUp.Clear();
        if (_shelved.Count > 0)
        {
            _shelved.Clear();
            UsabilityChanged?.Invoke();
        }
        Recheck();
    }

    // Wears whose answer never came, kept until the next full inventory read says
    // whether they went on.
    private readonly Dictionary<int, Wear> _gaveUp = new();

    // The character's level, alignment or stat screen was read anew: what the game
    // refused may be wearable now.
    public void LiftRefusals(string why)
    {
        if (_refused.Count == 0) return;
        _log?.Info(LogCategory, $"{Owner}: {why} — {_refused.Count} refused item(s) will be tried again");
        _refused.Clear();
        _saidUnwearable.Clear();
        UsabilityChanged?.Invoke();
        Recheck();
    }

    // The alignment was read. Only a change of it lifts a refusal: a refused wear
    // has the client read the alignment itself (it may be what moved), and lifting
    // on that read sent the same wear again, to be refused again, for as long as
    // the character stood by the hazard. The first reading is no change.
    public void NoteAlignmentRead(string? alignment)
    {
        if (string.IsNullOrWhiteSpace(alignment)
            || string.Equals(alignment, _alignment, StringComparison.OrdinalIgnoreCase)) return;
        bool first = _alignment is null;
        _alignment = alignment;
        if (!first) LiftRefusals($"the alignment changed to {alignment}");
    }

    private string? _alignment;

    // The game said an item is on (`You are now wearing X.`). The equipment
    // manager's own pairing reports the wear it sent for us; this also takes the
    // line itself, for an answer that came after the wait was given up.
    public void NoteWorn(string itemName)
    {
        string name = itemName?.Trim() ?? string.Empty;
        if (_pending is { } pending && string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            Confirm(pending);
            EndPending("the counter is on");
            return;
        }
        foreach ((int id, Wear late) in _late.ToList())
        {
            if (!string.Equals(late.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            _late.Remove(id);
            _shelved.Remove(id);
            Confirm(late);
            _log?.Info(LogCategory, $"{Owner}: '{late.Name}' went on after all — its slot is held until it is no longer needed");
            UsabilityChanged?.Invoke();
        }
    }

    // The game said a piece came off (`You have removed X.`). While a wear of ours
    // is out and the piece was in the slot it is going into, this is the piece the
    // wear pushed out: the line comes just ahead of the wear line.
    //
    // A counter of ours named here was taken off by other hands: the user's, or
    // another swap (an item readied for one use and put back). The slot is theirs
    // again, and the counter is left off until the character stands somewhere
    // else, so the two don't trade the slot back and forth in one room. Our own
    // restore lets go of the item before its `rem` goes out, so it never lands here.
    //
    // Not so a counter of ours pushed out by another wear of ours into the same
    // slot: the first candidate answered late, with the second already sent. That
    // is our own doing. The second takes over what the first had pushed out, and
    // only the first one's claim is let go.
    public void NoteRemoved(string itemName)
    {
        string name = itemName?.Trim() ?? string.Empty;
        List<Wear> waiting = _pending is { } pending ? _late.Values.Append(pending).ToList() : _late.Values.ToList();
        foreach (Wear wear in waiting)
            if (wear.InSlotBefore.Contains(name, StringComparer.OrdinalIgnoreCase)) wear.Evicted = name;
        foreach ((int id, Owned owned) in _owned.ToList())
        {
            if (!string.Equals(owned.Name, name, StringComparison.OrdinalIgnoreCase)
                || _pending?.Id == id || _late.ContainsKey(id)) continue;
            _confirmedOn.Remove(id);
            _owned.Remove(id);
            _equipment.DropSlotOverride(owned.Name, Owner);
            if (waiting.FirstOrDefault(w => SharePlaces(w.Id, id)) is { } pusher)
            {
                pusher.Evicted = owned.Displaced;
                _log?.Info(LogCategory,
                    $"{Owner}: '{owned.Name}' gave its place to '{pusher.Name}', which was sent while it had not answered");
                continue;
            }
            _takenOffHere = true;
            _log?.Info(LogCategory,
                $"{Owner}: '{owned.Name}' was taken off — its slot goes back to the gear sets, and it is left off until the next room");
        }

        // A place is free again: what had no room, or a piece stuck in its way, may go on.
        List<int> freed = _shelved
            .Where(static kv => kv.Value is SlotWearAnswer.NoRoom or SlotWearAnswer.OccupantStuck)
            .Select(static kv => kv.Key).ToList();
        if (freed.Count == 0) return;
        foreach (int id in freed) _shelved.Remove(id);
        UsabilityChanged?.Invoke();
    }

    private bool SharePlaces(int item, int other) =>
        _describe(item) is { } a && _describe(other) is { } b && EquipmentManager.SharePlaces(a.Slot, b.Slot);

    // Another character: nothing of the old one's is owned, and nothing is sent.
    public void Reset()
    {
        _owned.Clear();
        _confirmedOn.Clear();
        _late.Clear();
        _gaveUp.Clear();
        _takenOffHere = false;
        _alignment = null;
        _lastArrived = null;
        _refused.Clear();
        _shelved.Clear();
        _saidUnwearable.Clear();
        _saidNotSent.Clear();
        _saidRestoreHeld.Clear();
        _stepHeldOut = false;
        if (_pending is not null) EndPending("another character was loaded");
        UsabilityChanged?.Invoke();
    }

    private bool IsOn(int id)
    {
        if (_isWorn(id))
        {
            _confirmedOn.Remove(id);   // the inventory has caught up
            return true;
        }
        return _confirmedOn.Contains(id);
    }

    private void Confirm(Wear wear)
    {
        _confirmedOn.Add(wear.Id);
        _owned[wear.Id] = new Owned(wear.Name, wear.Evicted, wear.Spell);
        if (wear.Evicted is { } evicted)
            _log?.Info(LogCategory, $"{Owner}: '{wear.Name}' is on in place of '{evicted}', which goes back on afterwards");
    }

    // The game's answer to a wear the equipment manager sent for a slot claim, ours
    // or a location rule's of the same item.
    private void OnWearAnswered(string itemName, SlotWearAnswer answer)
    {
        if (answer == SlotWearAnswer.Worn)
        {
            NoteWorn(itemName);
            return;
        }
        Wear? wear = _pending is { } p && string.Equals(p.Name, itemName, StringComparison.OrdinalIgnoreCase)
            ? p
            : _late.Values.FirstOrDefault(l => string.Equals(l.Name, itemName, StringComparison.OrdinalIgnoreCase));
        if (wear is null) return;

        _owned.Remove(wear.Id);
        _late.Remove(wear.Id);
        _equipment.DropSlotOverride(wear.Name, Owner);
        string spell = $"{_spellName(wear.Spell) ?? "room spell"} (#{wear.Spell})";
        if (answer is SlotWearAnswer.Refused or SlotWearAnswer.CannotBeWorn)
        {
            _shelved.Remove(wear.Id);
            _refused[wear.Id] = answer;
            _log?.Info(LogCategory,
                $"{Owner}: the game refused '{wear.Name}' — going on with {spell} uncountered; "
                + "it is tried again after a level-up, an alignment change or a stat-screen read");
        }
        else
        {
            _shelved[wear.Id] = answer;
            _log?.Info(LogCategory, answer switch
            {
                SlotWearAnswer.NoRoom =>
                    $"{Owner}: no room to wear '{wear.Name}' — going on with {spell} uncountered; it is tried again when a piece comes off",
                SlotWearAnswer.OccupantStuck =>
                    $"{Owner}: '{wear.Name}' can't go on, the piece in its place can't be removed — going on with {spell} uncountered",
                _ =>
                    $"{Owner}: '{wear.Name}' isn't in the pack — going on with {spell} uncountered; it is looked for again at the next full inventory read",
            });
        }
        UsabilityChanged?.Invoke();
        if (ReferenceEquals(_pending, wear)) EndPending("the wear came to nothing");
    }

    // True when a wear went out for the room (the caller holds its step when asked
    // to and one is being held).
    private bool TryWear(Room room, bool hold, string when)
    {
        if (_pending is not null) return false;
        if (room.Spell <= 0 || !_enabled()) return false;
        IReadOnlyList<int> negators = _negatorsOf(room.Spell);
        if (negators.Count == 0 || negators.Any(IsOn)) return false;
        if (!negators.Any(_isCarried) || MasterSwitchOff?.Invoke() == true) return false;
        if (Pick(negators, room.Spell) is not { } pick) return false;

        string spell = $"{_spellName(room.Spell) ?? "room spell"} (#{room.Spell})";
        List<string> inSlot = _wornIn(pick.Slot).Select(static w => w.Name).ToList();
        // Through a kept sneak: the room's damage on every cast is the worse loss.
        switch (_equipment.ClaimSlot(pick.Name, Owner, urgent: true))
        {
            case SlotClaimResult.Sent:
                _log?.Info(LogCategory, $"{Owner}: wearing '{pick.Name}' for {spell} {when} {room.Key}");
                // A wear mid-fight stops the attack; the combat engine picks it up again.
                _gearCommandSent?.Invoke();
                break;
            case SlotClaimResult.OnItsWay:
                _log?.Info(LogCategory,
                    $"{Owner}: '{pick.Name}' is already being put on — waiting for it for {spell} {when} {room.Key}");
                break;
            case SlotClaimResult.AlreadyWorn:
                // On, by the equipment manager's reading: held from here, nothing to wait for.
                _owned[pick.Id] = new Owned(pick.Name, null, room.Spell);
                _confirmedOn.Add(pick.Id);
                return false;
            case SlotClaimResult.NotSent:
                if (_saidNotSent.Add(pick.Id))
                    _log?.Info(LogCategory,
                        $"{Owner}: '{pick.Name}' would negate {spell} in {room.Key}, but commands are being held back just now — "
                        + "nothing was sent; it is tried again when they go out again");
                return false;
            default:
                _log?.Info(LogCategory,
                    $"{Owner}: '{pick.Name}' would negate {spell} in {room.Key}, but it can't be put on (not in the pack as read) — going on uncountered");
                _shelved[pick.Id] = SlotWearAnswer.NotInPack;
                UsabilityChanged?.Invoke();
                return false;
        }
        _owned[pick.Id] = new Owned(pick.Name, null, room.Spell);
        BeginPending(new Wear(pick.Id, pick.Name, room.Spell, inSlot), hold);
        return true;
    }

    // Which of the carried items that negate the spell goes on. Several can: the
    // phoenix feather and the magma amulet negate the same two spells from the same
    // slot. One that fills a free place comes before one that pushes a piece out;
    // then the one that leaves the character best armoured, counting what it pushes
    // out (armour class first, then damage resistance, then the lower item number
    // so the choice is steady). For that pair it is the feather, 10 / 1 against the
    // amulet's 0 / 0, which is the order the user gave.
    private RoomSpellCounterItem? Pick(IReadOnlyList<int> negators, int spell)
    {
        RoomSpellCounterItem? best = null;
        (int Free, int Ac, int Dr) bestRank = default;
        foreach (int id in negators)
        {
            if (!_isCarried(id) || _refused.ContainsKey(id) || _shelved.ContainsKey(id)) continue;
            if (_describe(id) is not { } item) continue;
            if (!item.CanWear)
            {
                if (_saidUnwearable.Add(id))
                    _log?.Info(LogCategory,
                        $"{Owner}: '{item.Name}' negates {_spellName(spell) ?? "room spell"} (#{spell}) but this character can't wear it — not used");
                continue;
            }
            (int Free, int Ac, int Dr) rank = Rank(item);
            if (best is null || rank.CompareTo(bestRank) > 0
                || (rank.CompareTo(bestRank) == 0 && item.Id < best.Value.Id))
            {
                best = item;
                bestRank = rank;
            }
        }
        return best;
    }

    // In a pair that is full the game picks which piece comes out, so the better
    // of the two is the one counted as lost.
    private (int Free, int Ac, int Dr) Rank(RoomSpellCounterItem item)
    {
        IReadOnlyList<RoomSpellCounterItem> inSlot = _wornIn(item.Slot);
        if (inSlot.Count < EquipmentManager.PlacesFor(item.Slot))
            return (1, item.ArmourClass, item.DamageResist);
        RoomSpellCounterItem lost = inSlot
            .OrderByDescending(static w => w.ArmourClass).ThenByDescending(static w => w.DamageResist).First();
        return (0, item.ArmourClass - lost.ArmourClass, item.DamageResist - lost.DamageResist);
    }

    private void BeginPending(Wear wear, bool hold)
    {
        _pending = wear;
        int token = ++_pendingToken;
        if (hold) Hold(wear.Name);
        _schedule(AnswerCap, () =>
        {
            if (!ReferenceEquals(_pending, wear) || token != _pendingToken) return;
            // Set aside, and the claim kept a little longer in case the answer is
            // only late: the item may be on by then, with a piece of the user's
            // pushed out that has to go back on afterwards.
            _shelved[wear.Id] = null;
            _late[wear.Id] = wear;
            _stepHeldOut = true;
            _log?.Info(LogCategory,
                $"{Owner}: no answer to the wear of '{wear.Name}' in {AnswerCap.TotalSeconds:0}s — going on; "
                + "it isn't tried again until the inventory is next read in full");
            UsabilityChanged?.Invoke();
            EndPending("no answer in time");
            _schedule(LateAnswerWindow, () =>
            {
                if (!_late.TryGetValue(wear.Id, out Wear? still) || !ReferenceEquals(still, wear)) return;
                _late.Remove(wear.Id);
                if (_isWorn(wear.Id))
                {
                    // No line, but the inventory has it on: ours to give back later.
                    Confirm(wear);
                    return;
                }
                _owned.Remove(wear.Id);
                _gaveUp[wear.Id] = wear;
                _equipment.DropSlotOverride(wear.Name, Owner);
            });
        });
    }

    private void Hold(string item)
    {
        if (_holding) return;
        _holding = true;
        _coordinator.AssertGate(MovementCoordinator.CounterWearGate, AsserterName, $"waiting for '{item}' to go on");
    }

    private void EndPending(string why)
    {
        _pending = null;
        if (!_holding) return;
        _holding = false;
        _coordinator.ClearGate(MovementCoordinator.CounterWearGate, AsserterName, why);
    }

    // needed: the spells of the room stood in and the rooms next to it (and, sneaking
    // past things, of the rooms the plan comes to next). nextStep: the spells in
    // and next to the room the plan's next step lands in, which keep a counter on
    // and put none on. empty: a gear command may go out here, which sneaking past
    // things is only true of a room with no NPC.
    private void ReleaseUnneeded(Room here, HashSet<int> needed, HashSet<int> nextStep, bool empty)
    {
        _keptForNextStep.Clear();
        bool For(HashSet<int> spells, int id) => spells.Any(spell => _negatorsOf(spell).Contains(id));
        foreach ((int id, Owned owned) in _owned)
            if (!For(needed, id) && For(nextStep, id)) _keptForNextStep.Add(owned.Name);
        if (!_owned.Keys.Any(id => !For(needed, id) && !For(nextStep, id))) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        foreach ((int id, Owned owned) in _owned.ToList())
        {
            if (_pending?.Id == id || _late.ContainsKey(id)) continue;
            if (For(needed, id) || For(nextStep, id))
            {
                _saidRestoreHeld.Remove(id);
                continue;
            }
            if (!IsOn(id))
            {
                // Taken off by hand, or never went on: there is nothing to put back.
                _owned.Remove(id);
                _equipment.DropSlotOverride(owned.Name, Owner);
                _log?.Info(LogCategory, $"{Owner}: '{owned.Name}' is no longer on — its slot goes back to the gear sets");
                continue;
            }
            // Ownership ends when the commands have gone out, not before: a slot
            // waiting for an empty room is decided again there, or sooner if the
            // item is needed again.
            if (!empty)
            {
                _waitingForEmptyRoom = true;
                if (_saidRestoreHeld.Add(id))
                    _log?.Info(LogCategory,
                        $"{Owner}: '{owned.Name}' is no longer needed at {here.Key}, but there is an NPC here to sneak past — "
                        + "its slot is given back in the next room with none");
                continue;
            }
            // Never through a sneak the guard is keeping: the wear has the room's
            // damage at stake and the restore has nothing, so it isn't worth a
            // backstab opener. Held, the item stays owned, and this is asked again
            // when the guard lets go (Recheck), by when it may be needed after all.
            switch (_equipment.ClearSlotOverride(owned.Name, Owner, owned.Displaced, queueIfHeld: false))
            {
                case SlotReleaseResult.Held:
                    if (_saidRestoreHeld.Add(id))
                        _log?.Info(LogCategory,
                            $"{Owner}: '{owned.Name}' is no longer needed at {here.Key}, but a sneak is being kept — "
                            + "its slot is given back when the sneak is let go");
                    continue;
                case SlotReleaseResult.ReleasedWithCommands:
                    _log?.Info(LogCategory,
                        $"{Owner}: no room with {_spellName(owned.Spell) ?? "that spell"} (#{owned.Spell}) or another spell '{owned.Name}' negates "
                        + $"at or next to {here.Key} — its slot was given back");
                    _confirmedOn.Remove(id);
                    _gearCommandSent?.Invoke();
                    break;
            }
            _owned.Remove(id);
            _saidRestoreHeld.Remove(id);
        }
    }

    // The counters that are on only because the plan's next step is in or next to a
    // room they are for. For the bug report.
    public IReadOnlyCollection<string> KeptForNextStep => _keptForNextStep;
    private readonly List<string> _keptForNextStep = new();
}
