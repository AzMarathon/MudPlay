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
// The wear and the restore are EquipmentManager's slot overrides, the mechanism a
// location rule uses: while the slot is owned the gear sets skip it, and giving it
// back puts on whatever the set in force has for it. So another swap in the
// meantime doesn't take the counter off, and a set changed while it was on is the
// one honoured on the way out.
//
// When it goes on:
//   - before a step an engine plans (walk-to, loop, Auto-Lair, errands), from the
//     engine's ready check, which holds the step until the game says the item is on
//     or refuses it, and no longer than AnswerCap;
//   - ahead of a typed step, with no hold (the move is already on its way out);
//   - on arrival, for every move the client couldn't get ahead of: a follower
//     dragged in by the leader, a flee step, a teleport.
// When it comes off: once no room within KeepWithinSteps of the character (one step,
// with nothing moving it) carries a spell the item negates. The distance is what
// keeps a route that weaves in and out of such rooms, or a loop that circles beside
// them, from swapping every few steps. Standing in such a room it stays on.
public sealed class RoomSpellCounterWear
{
    // Shares the Equipment category, like the location rules: the wear and the
    // revert are logged there by EquipmentManager, and these lines say why.
    public const string LogCategory = EquipmentManager.LogCategory;
    public const string AsserterName = nameof(RoomSpellCounterWear);

    // The name the slot is claimed under, as it reads in EquipmentManager's log lines.
    private const string Owner = "room-spell counter";

    // How long a step waits for the game to answer the wear: the sneak-first hold's
    // own bound, so a line that never comes can't strand a walk.
    public static readonly TimeSpan AnswerCap = TimeSpan.FromSeconds(3);

    // While something is moving the character, the counter stays on until every
    // room it is needed in is further off than this.
    public const int KeepWithinSteps = 5;

    // With nothing moving it, only the room stood in and the ones next to it count:
    // a walk that ends outside such rooms gives the slot back there.
    public const int KeepWithinStepsIdle = 1;

    private readonly EquipmentManager _equipment;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _enabled;
    private readonly Func<RoomKey, Room?> _roomOf;
    private readonly Func<int, IReadOnlyList<int>> _negatorsOf;
    private readonly Func<int, RoomSpellCounterItem?> _describe;
    private readonly Func<int, bool> _isWorn;
    private readonly Func<int, bool> _isCarried;
    private readonly Func<EquipmentSlot, RoomSpellCounterItem?> _wornIn;
    private readonly Func<int, string?> _spellName;
    private readonly Func<bool> _beingMoved;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action? _gearCommandSent;
    private readonly LogService? _log;

    // What this class put on and still answers for: the item, the piece that was
    // in its slot, and the spell it went on for.
    private readonly record struct Owned(string Name, string? Displaced, int Spell);
    private readonly Dictionary<int, Owned> _owned = new();

    // The wear whose answer hasn't come yet, and whether a step is held for it.
    private (int Id, string Name)? _pending;
    private bool _holding;
    private int _pendingToken;

    // Items the game refused: not tried again for this character. Items whose wear
    // drew no answer: not tried again until the next room.
    private readonly HashSet<int> _refused = new();
    private readonly HashSet<int> _unanswered = new();

    // A carried item that negates the room's spell and can't be worn is said once.
    private readonly HashSet<int> _saidUnwearable = new();

    // The master switch (true = off): off, nothing is put on and nothing is given
    // back. What is worn stays as it is until the switch is on again (OnArrived is
    // run for the room stood in then).
    public Func<bool>? MasterSwitchOff { get; set; }

    public RoomSpellCounterWear(
        EquipmentManager equipment,
        MovementCoordinator coordinator,
        Func<bool> enabled,
        Func<RoomKey, Room?> roomOf,
        Func<int, IReadOnlyList<int>> negatorsOf,
        Func<int, RoomSpellCounterItem?> describe,
        Func<int, bool> isWorn,
        Func<int, bool> isCarried,
        Func<EquipmentSlot, RoomSpellCounterItem?> wornIn,
        Func<int, string?> spellName,
        Func<bool> beingMoved,
        Action<TimeSpan, Action> schedule,
        Action? gearCommandSent = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(roomOf);
        ArgumentNullException.ThrowIfNull(negatorsOf);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(isWorn);
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(wornIn);
        ArgumentNullException.ThrowIfNull(spellName);
        ArgumentNullException.ThrowIfNull(beingMoved);
        ArgumentNullException.ThrowIfNull(schedule);
        _equipment = equipment;
        _coordinator = coordinator;
        _enabled = enabled;
        _roomOf = roomOf;
        _negatorsOf = negatorsOf;
        _describe = describe;
        _isWorn = isWorn;
        _isCarried = isCarried;
        _wornIn = wornIn;
        _spellName = spellName;
        _beingMoved = beingMoved;
        _schedule = schedule;
        _gearCommandSent = gearCommandSent;
        _log = log;
    }

    // What this class has on, for the bug report: the item, what it displaced and
    // the spell it went on for.
    public IReadOnlyList<(string Item, string? Displaced, int Spell)> OwnedSnapshot() =>
        _owned.Values.Select(static o => (o.Name, o.Displaced, o.Spell)).ToList();

    // The wear still waiting for the game's answer, null when none is.
    public string? PendingItem => _pending?.Name;

    // True when the client is switched on to wear this carried item for a room: the
    // test a route is planned on, so the planner and the wear agree. An item the
    // game refused, or one the character can't wear, opens no room.
    public bool WillWear(int itemId) =>
        _enabled() && MasterSwitchOff?.Invoke() != true
        && !_refused.Contains(itemId)
        && _describe(itemId) is { CanWear: true };

    // Asked by the walker and the loop before a planned step goes out, with the
    // room the step leads into. False holds the step: the wear has gone out and the
    // game hasn't answered it. The engine is driven on again when the hold clears.
    public bool ReadyToEnter(RoomKey? next)
    {
        if (_pending is { } waiting)
        {
            // A wear already out (sent on arrival) is waited for like one sent here.
            Hold(waiting.Name);
            return false;
        }
        if (next is not { } key || _roomOf(key) is not { } room) return true;
        return !TryWear(room, hold: true, "before entering");
    }

    // A typed step is already on its way out: the wear goes ahead of it on the
    // wire, as a typed step's `sn` does, and nothing is held.
    public void BeforeTypedStep(RoomKey next)
    {
        if (_roomOf(next) is { } room) TryWear(room, hold: false, "ahead of the typed step into");
    }

    // The character stands in a new room. A move the client couldn't get ahead of
    // (a leader's drag, a flee step, a teleport) is countered here, one cast late at
    // worst; then whatever is no longer needed nearby is given back.
    public void OnArrived(RoomKey room)
    {
        _unanswered.Clear();
        if (_roomOf(room) is not { } here) return;
        TryWear(here, hold: false, "on arriving in");
        ReleaseUnneeded(here);
    }

    // The walk or loop ended: outside such rooms the slot goes back now, not when
    // the character next happens to move five steps off.
    public void OnMovementEnded(RoomKey? room)
    {
        if (room is { } key && _roomOf(key) is { } here) ReleaseUnneeded(here);
    }

    // The game said an item is on (`You are now wearing X.`).
    public void NoteWorn(string itemName)
    {
        if (_pending is not { } pending
            || !string.Equals(pending.Name, itemName?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        _refused.Remove(pending.Id);
        EndPending("the counter is on");
    }

    // The game refused a wear. The line names no item, so it is taken for ours only
    // while ours is the one waiting for an answer.
    public void NoteWearRefused()
    {
        if (_pending is not { } pending) return;
        _refused.Add(pending.Id);
        _owned.Remove(pending.Id);
        _equipment.DropSlotOverride(pending.Name, Owner);
        _log?.Info(LogCategory,
            $"{Owner}: the game refused '{pending.Name}' — going on with the room's spell uncountered; it won't be tried again");
        EndPending("the wear was refused");
    }

    // Another character: nothing of the old one's is owned, and nothing is sent.
    public void Reset()
    {
        _owned.Clear();
        _refused.Clear();
        _unanswered.Clear();
        _saidUnwearable.Clear();
        if (_pending is not null) EndPending("another character was loaded");
    }

    private bool TryWear(Room room, bool hold, string when)
    {
        if (_pending is not null) return false;
        if (room.Spell <= 0 || !_enabled() || MasterSwitchOff?.Invoke() == true) return false;
        IReadOnlyList<int> negators = _negatorsOf(room.Spell);
        if (negators.Count == 0 || negators.Any(_isWorn)) return false;
        if (Pick(negators, room.Spell) is not { } pick) return false;

        string? displaced = _wornIn(pick.Slot)?.Name;
        string spell = $"{_spellName(room.Spell) ?? "room spell"} (#{room.Spell})";
        // Through a kept sneak: the room's damage on every cast is the worse loss.
        if (!_equipment.SetSlotOverride(pick.Name, Owner, urgent: true))
        {
            _log?.Info(LogCategory,
                $"{Owner}: '{pick.Name}' would negate {spell} in {room.Key}, but it can't be put on (not in the pack as read) — going on uncountered");
            _unanswered.Add(pick.Id);
            return false;
        }
        _owned[pick.Id] = new Owned(pick.Name, displaced, room.Spell);
        _log?.Info(LogCategory,
            $"{Owner}: wearing '{pick.Name}' for {spell} {when} {room.Key}"
            + (displaced is null ? string.Empty : $", in place of '{displaced}'"));
        // A wear mid-fight stops the attack; the combat engine picks it up again.
        _gearCommandSent?.Invoke();
        BeginPending(pick, hold);
        return true;
    }

    // Which of the carried items that negate the spell goes on. Several can: the
    // phoenix feather and the magma amulet negate the same two spells from the same
    // slot. The one that leaves the character best armoured is taken, counting what
    // it pushes out of its slot (armour class first, then damage resistance, then
    // the lower item number so the choice is steady). For that pair it is the
    // feather, 10 / 1 against the amulet's 0 / 0, which is the order the user gave.
    private RoomSpellCounterItem? Pick(IReadOnlyList<int> negators, int spell)
    {
        RoomSpellCounterItem? best = null;
        (int Ac, int Dr) bestGain = default;
        foreach (int id in negators)
        {
            if (!_isCarried(id) || _refused.Contains(id) || _unanswered.Contains(id)) continue;
            if (_describe(id) is not { } item) continue;
            if (!item.CanWear)
            {
                if (_saidUnwearable.Add(id))
                    _log?.Info(LogCategory,
                        $"{Owner}: '{item.Name}' negates {_spellName(spell) ?? "room spell"} (#{spell}) but this character can't wear it — not used");
                continue;
            }
            (int Ac, int Dr) gain = Gain(item);
            if (best is null || gain.CompareTo(bestGain) > 0
                || (gain.CompareTo(bestGain) == 0 && item.Id < best.Value.Id))
            {
                best = item;
                bestGain = gain;
            }
        }
        return best;
    }

    private (int Ac, int Dr) Gain(RoomSpellCounterItem item) =>
        _wornIn(item.Slot) is { } was
            ? (item.ArmourClass - was.ArmourClass, item.DamageResist - was.DamageResist)
            : (item.ArmourClass, item.DamageResist);

    private void BeginPending(RoomSpellCounterItem pick, bool hold)
    {
        _pending = (pick.Id, pick.Name);
        int token = ++_pendingToken;
        if (hold) Hold(pick.Name);
        _schedule(AnswerCap, () =>
        {
            if (_pending is not { } still || token != _pendingToken) return;
            _unanswered.Add(still.Id);
            if (_holding)
                _log?.Info(LogCategory,
                    $"{Owner}: no answer to the wear of '{still.Name}' in {AnswerCap.TotalSeconds:0}s — going on");
            EndPending("no answer in time");
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

    private void ReleaseUnneeded(Room here)
    {
        if (_owned.Count == 0 || MasterSwitchOff?.Invoke() == true) return;
        HashSet<int> near = SpellsWithin(here, _beingMoved() ? KeepWithinSteps : KeepWithinStepsIdle);
        foreach ((int id, Owned owned) in _owned.ToList())
        {
            if (_pending?.Id == id) continue;
            if (near.Any(spell => _negatorsOf(spell).Contains(id))) continue;
            _owned.Remove(id);
            if (!_isWorn(id))
            {
                // Taken off by hand, or never went on: there is nothing to put back.
                _equipment.DropSlotOverride(owned.Name, Owner);
                _log?.Info(LogCategory, $"{Owner}: '{owned.Name}' is no longer on — its slot goes back to the gear sets");
                continue;
            }
            _log?.Info(LogCategory,
                $"{Owner}: no room with {_spellName(owned.Spell) ?? "that spell"} (#{owned.Spell}) or another spell '{owned.Name}' negates "
                + $"within reach of {here.Key} — giving its slot back");
            _equipment.ClearSlotOverride(owned.Name, Owner, owned.Displaced);
            _gearCommandSent?.Invoke();
        }
    }

    // The spells of the rooms at most `steps` exits from here, the room itself
    // included.
    private HashSet<int> SpellsWithin(Room here, int steps)
    {
        HashSet<int> spells = new();
        HashSet<RoomKey> seen = new() { here.Key };
        List<Room> ring = new() { here };
        for (int depth = 0; ring.Count > 0; depth++)
        {
            List<Room> next = new();
            foreach (Room room in ring)
            {
                if (room.Spell > 0) spells.Add(room.Spell);
                if (depth == steps) continue;
                foreach (RoomExit exit in room.Exits.Values)
                    if (seen.Add(exit.Target) && _roomOf(exit.Target) is { } beyond) next.Add(beyond);
            }
            ring = next;
        }
        return spells;
    }
}
