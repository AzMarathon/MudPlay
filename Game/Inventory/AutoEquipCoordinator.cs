using System;
using System.ComponentModel;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Watches the live character posture and auto-swaps the trigger-purposed gear
// sets at the moments the Equipment Manager arms. It is a pure subscriber — it
// reads PlayerState (position) and the HealthManager's recovery gates, and never
// writes any observed field, so it sits outside the single-writer ownership
// model. When a moment fires it resolves the matching enabled EquipmentSet
// through the live EquipmentSettings and hands its id to
// EquipmentManager.ApplyBySetId.
//
// Signal mapping:
//   - Pre-rest HP / Mana: position goes to Resting; the held recovery gate
//     (HP vs MA) disambiguates which set is wanted. Meditating is a mana
//     recovery, so it maps to Pre-rest Mana.
//   - Default: only when the character is DONE recovering (stands up out of a
//     rest with neither rest gate still held) AND a pre-rest swap set is enabled
//     (so we're actually swapping back from one), or when a loop / Auto-Lair run
//     begins (OnLoopStarted). Combat entry does NOT swap to Default by default — a
//     fight that interrupts a rest keeps its pre-rest loadout and reverts only once
//     recovered, per the user's rule (report paradigm-20260826-132742). The one
//     opt-out is EquipmentSettings.SwapToDefaultOnCombat (Equipment Manager's "Don't
//     swap to default upon entering combat" UNchecked): then a rest-interrupting
//     fight swaps to Default on entry (OnCombatChanged) and swaps back to the
//     pre-rest set on room-clear if still gated. The remaining Default path —
//     re-wearing on death-pile recovery — lives in the recovery engine, gated by
//     its own setting.
//
// The Backstab set isn't auto-fired here yet — it needs the combat engine's
// "room clear → sneak → surprise round" sequencing that isn't built; until then
// Backstab is editable and manually / remotely appliable. The decision halves
// (ClassifyRest, ResolveTarget) are pure and unit-tested; the subscription
// plumbing is smoke-tested live.
//
// A fired moment is held until the worn loadout is known (InventoryManager
// has parsed at least one full 'i' dump). The apply engine diffs the desired
// set against the live worn set, and an empty worn set reads as "nothing worn"
// rather than "unknown", so firing before the first dump would emit a wear for
// every set item — including one already worn (the game answers "You do not
// have X left unequipped."). Manual applies (the Workshop button,
// @equip-<set>) carry explicit intent and aren't gated here.
public sealed class AutoEquipCoordinator : IDisposable
{
    private readonly PlayerState _player;
    private readonly Func<EquipmentSettings> _readEquipment;
    private readonly Func<bool> _hpGateAsserted;
    private readonly Func<bool> _maGateAsserted;
    private readonly Func<string, EquipResult> _applyBySetId;
    private readonly Func<bool> _wornLoadoutKnown;
    private readonly Func<bool> _isAutoEnabled;
    // Room predicates + a live "is the nav engine moving?" probe for the movement /
    // bossing sets. Null (unwired / tests) means "never" — the new sets stay inert.
    private readonly Func<Game.Map.RoomKey, bool>? _isBossRoom;
    private readonly Func<Game.Map.RoomKey, bool>? _isLair;
    private readonly Func<bool>? _isMoving;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    private PlayerPosition _lastPosition;

    // True while the "While Moving" set is the active auto loadout (worn because the
    // nav engine is travelling). Cleared when we swap away for combat / a boss room /
    // arrival, so the combat-entry and arrival handlers know whether a movement-gear
    // revert is owed.
    private bool _inMovementSet;

    // Last room the tracker confirmed us into, so combat-entry and the movement
    // handlers can tell whether the current room is a boss room (gear precedence).
    private Game.Map.RoomKey? _currentRoom;

    // An item-cast buff (ItemCastSequencer) temporarily borrows an equip slot: it
    // removes the worn gear, wields the cast item, uses it, then re-equips the gear
    // itself. That transient swap — and the rest it breaks — can trip a posture
    // auto-equip fire that ALSO restores the slot, double-sending the wear
    // (report paradigm-20260815-130733: an "eq griffon shield" restore followed by a
    // redundant "wear griffon shield" the game rejects). Hold auto-equip fires briefly
    // after an item-cast swap so the sequencer's own restore owns the slot — the same
    // "leave the slot to whoever's swapping it" courtesy _combatOwnsWeaponSlot gives
    // the combat engine.
    private DateTimeOffset _lastItemCastSwapAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ItemCastSwapSuppressWindow = TimeSpan.FromSeconds(4);

    // Recovery-complete swaps back to the Default set in-room (OnRecoveryComplete),
    // and the stand-up that immediately follows would fire a SECOND, redundant
    // Default swap (report paradigm-20260827-125103: the gear swapper double-swapping
    // — a Default apply, then another ~1s later as the walker stepped out). Stamp
    // when a Default actually applies and suppress the stand-up Default within a short
    // window of it, so recovery→Default swaps exactly once. The stand-up path still
    // fires when nothing recently swapped Default (a stand that OnRecoveryComplete
    // didn't cover — e.g. recovery gated off in combat).
    private DateTimeOffset _lastDefaultAppliedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DefaultReapplySuppressWindow = TimeSpan.FromSeconds(4);

    // Set true after we swap to Default because a fight interrupted a rest (only when
    // EquipmentSettings.SwapToDefaultOnCombat is on). On room-clear it tells us to swap
    // back to the pre-rest set if the rest still isn't done. Reset when the box is off
    // so toggling it mid-fight can't strand the flag.
    private bool _swappedToDefaultForCombat;

    public AutoEquipCoordinator(
        PlayerState player,
        Func<EquipmentSettings> readEquipment,
        Func<bool> hpGateAsserted,
        Func<bool> maGateAsserted,
        Func<string, EquipResult> applyBySetId,
        Func<bool> wornLoadoutKnown,
        Func<bool> isAutoEnabled,
        LogService? log = null,
        Func<DateTimeOffset>? now = null,
        Func<Game.Map.RoomKey, bool>? isBossRoom = null,
        Func<Game.Map.RoomKey, bool>? isLair = null,
        Func<bool>? isMoving = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(readEquipment);
        ArgumentNullException.ThrowIfNull(hpGateAsserted);
        ArgumentNullException.ThrowIfNull(maGateAsserted);
        ArgumentNullException.ThrowIfNull(applyBySetId);
        ArgumentNullException.ThrowIfNull(wornLoadoutKnown);
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        _player = player;
        _readEquipment = readEquipment;
        _hpGateAsserted = hpGateAsserted;
        _maGateAsserted = maGateAsserted;
        _applyBySetId = applyBySetId;
        _wornLoadoutKnown = wornLoadoutKnown;
        _isAutoEnabled = isAutoEnabled;
        _isBossRoom = isBossRoom;
        _isLair = isLair;
        _isMoving = isMoving;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _lastPosition = player.Position;
        _player.PropertyChanged += OnPlayerChanged;
    }

    // Note that an item-cast buff swap just began (ItemCastSequencer): its
    // equip / use / restore dance owns whatever slot it borrowed, so auto-equip
    // fires are held for a short window (see _lastItemCastSwapAt) rather than
    // double-restoring the slot.
    public void NoteItemCastSwap() => _lastItemCastSwapAt = _now();

    // ----- pure decision logic (unit-tested) ------------------------------

    // The pre-rest trigger a transition into `to` implies, or null when the new
    // posture isn't a rest / meditate. A held HP gate marks an HP rest;
    // otherwise a held MA gate marks a mana rest; a plain rest with neither gate
    // still known defaults to HP (the common case). Meditation is always a mana
    // recovery.
    internal static EquipTriggerType? ClassifyRest(PlayerPosition to, bool hpGate, bool maGate) => to switch
    {
        PlayerPosition.Meditating => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting when maGate && !hpGate => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting => EquipTriggerType.PreRestHp,
        _ => null,
    };

    // The set id a type moment should apply given the live config, or null when
    // it shouldn't fire — no set exists for the type, the set is disabled, or it
    // has no stable id.
    internal static string? ResolveTarget(EquipmentSettings cfg, EquipTriggerType type)
    {
        EquipmentSet? set = cfg.Sets.FirstOrDefault(s => s.Trigger == type);
        if (set is not { Enabled: true }) return null;
        return string.IsNullOrEmpty(set.Id) ? null : set.Id;
    }

    // ----- signal subscription (UI plumbing) ------------------------------

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.InCombat))
        {
            OnCombatChanged(_player.InCombat);
            return;
        }
        if (e.PropertyName != nameof(PlayerState.Position)) return;
        OnPositionChanged(_lastPosition, _player.Position);
        _lastPosition = _player.Position;
    }

    // Opt-in (Equipment Manager's "Don't swap to default upon entering combat"
    // UNchecked → SwapToDefaultOnCombat true): a fight that interrupts a rest is
    // fought in the Default combat loadout instead of the pre-rest set. On combat
    // entry, if we're recovering in a pre-rest set, swap to Default for the fight; on
    // room-clear, if the rest still isn't satisfied, swap back to the pre-rest set the
    // held gate wants (HealthManager re-issues the rest itself). With the box checked
    // (the default) this is inert — the character keeps its pre-rest loadout through the
    // fight and reverts to Default only once recovered, the long-standing rule.
    private void OnCombatChanged(bool inCombat)
    {
        if (inCombat)
        {
            // In a boss room the fight is fought in the Bossing set — it wins over the
            // movement / rest swaps. The pre-step swap usually already wore it; this is
            // the idempotent backstop for a boss room entered without a planned step.
            if (CurrentRoomIsBoss() && EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                _inMovementSet = false;
                Fire(EquipTriggerType.Bossing);
                return;
            }
            // A hostile appeared while we were travelling in the movement set — swap to
            // Default and let combat engage. Independent of SwapToDefaultOnCombat, which
            // governs only the rest-interrupt case below.
            if (_inMovementSet)
            {
                _inMovementSet = false;
                _log?.Info(EquipmentManager.LogCategory,
                    "hostiles recognized while moving — swapping to Default for the fight");
                Fire(EquipTriggerType.Default);
                return;
            }
        }

        if (!_readEquipment().SwapToDefaultOnCombat)
        {
            // Toggling the box off mid-fight must not strand the restore flag.
            _swappedToDefaultForCombat = false;
            return;
        }

        if (inCombat)
        {
            // Only when a rest actually swapped us into a pre-rest set: we're recovering
            // (a rest gate is held) AND a pre-rest swap set is enabled. Otherwise there's
            // nothing to swap away from — and nothing to restore on clear.
            if (_swappedToDefaultForCombat) return;
            if (!(_hpGateAsserted() || _maGateAsserted())) return;
            if (!UsingRestingSwapSets()) return;
            _swappedToDefaultForCombat = true;
            _log?.Info(EquipmentManager.LogCategory,
                "combat interrupted a rest — swapping to Default for the fight "
                + "(swap-to-default-on-combat enabled)");
            Fire(EquipTriggerType.Default);
        }
        else
        {
            if (!_swappedToDefaultForCombat) return;
            _swappedToDefaultForCombat = false;
            // Recovered during the fight → gates clear → stay in Default. Still gated →
            // swap back to the set the held gate wants so the resumed rest wears it (HP
            // gate wins when both are held, mirroring ClassifyRest's plain-rest default).
            EquipTriggerType? restType =
                _hpGateAsserted() ? EquipTriggerType.PreRestHp
                : _maGateAsserted() ? EquipTriggerType.PreRestMana
                : (EquipTriggerType?)null;
            if (restType is { } rt)
            {
                _log?.Info(EquipmentManager.LogCategory,
                    $"room cleared, still rest-gated — swapping back to '{rt}' to resume the rest");
                Fire(rt);
            }
        }
    }

    // A loop or Auto-Lair run just began — swap to the Default (baseline) set.
    // Wired from AppServices to LoopRunner's ReachedFirstWaypoint and AutoLair's
    // ActiveChanged(true). Unconditional (subject only to Fire's own gates): a run
    // starting means we're moving out under normal combat gear, whatever we were
    // wearing while idle / resting beforehand.
    public void OnLoopStarted()
    {
        // With a movement set configured (enabled + non-empty), the movement-state
        // hook (OnMovementStarted) wears it once the run is actually travelling — so
        // don't pre-empt with a Default swap here. Without one, keep the long-standing
        // Default-at-loop-start behavior.
        if (MovementSetActive()) return;
        Fire(EquipTriggerType.Default);
    }

    // ----- movement / boss / lair sets (pushed from AppServices) ----------

    // The nav engine started (or resumed) travelling and we're neither fighting nor
    // resting — wear the movement set. No-op unless a movement set is enabled + has
    // items, we're not already in it, and the current room isn't a boss room (Bossing
    // owns gear there). Idempotent: re-entry while already in the set does nothing.
    public void OnMovementStarted()
    {
        if (_inMovementSet) return;
        if (_player.InCombat) return;
        if (_hpGateAsserted() || _maGateAsserted()) return;
        if (CurrentRoomIsBoss()) return;
        if (!MovementSetActive()) return;
        _inMovementSet = true;
        _log?.Info(EquipmentManager.LogCategory, "nav engine moving — wearing the While Moving set");
        Fire(EquipTriggerType.WhileMoving);
    }

    // The nav engine went fully idle — a walk-to reached its destination, or a loop /
    // Auto-Lair run stopped. Revert the movement gear to Default. A pause for combat or
    // rest is NOT idle (the run stays live), so those keep their own gear; only a true
    // stop reverts.
    public void OnMovementStopped()
    {
        if (!_inMovementSet) return;
        _inMovementSet = false;
        if (_player.InCombat || _hpGateAsserted() || _maGateAsserted() || CurrentRoomIsBoss()) return;
        _log?.Info(EquipmentManager.LogCategory, "movement stopped — reverting the While Moving set to Default");
        Fire(EquipTriggerType.Default);
    }

    // The tracked room changed (a confirmed transition). Drives the Bossing set:
    // entering a boss room wears it, leaving one reverts — to the movement set if we're
    // still travelling, otherwise Default (the user's "back to default on leaving").
    public void OnRoomChanged(Game.Map.RoomKey? previous, Game.Map.RoomKey current)
    {
        _currentRoom = current;
        bool curBoss = _isBossRoom is { } cb && cb(current);
        bool prevBoss = _isBossRoom is { } pb && previous is { } pr && pb(pr);
        if (curBoss)
        {
            // Backstop for a boss room entered without a pre-step swap (the pre-move
            // hook usually wore Bossing already; ApplySet no-ops if it's on).
            if (EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                _inMovementSet = false;
                Fire(EquipTriggerType.Bossing);
            }
            return;
        }
        if (prevBoss)
        {
            if (MovementSetActive() && (_isMoving?.Invoke() ?? false) && !_player.InCombat)
            {
                _inMovementSet = true;
                _log?.Info(EquipmentManager.LogCategory, "left the boss room — back to the While Moving set");
                Fire(EquipTriggerType.WhileMoving);
            }
            else
            {
                _log?.Info(EquipmentManager.LogCategory, "left the boss room — reverting to Default");
                Fire(EquipTriggerType.Default);
            }
        }
    }

    // About to send a step INTO `next`. Swap BEFORE the wire move so we land already
    // geared — the swap's wear/eq commands queue ahead of the move on the serialized
    // wire (same ordering the pre-move backstab prep relies on). A boss room wears the
    // Bossing set; a known lair wears Default when the movement set's "swap before
    // lairs" option is on and we're currently travelling in the movement set.
    public void OnAboutToEnterRoom(Game.Map.RoomKey next)
    {
        if (_player.InCombat) return;
        if (_isBossRoom is { } boss && boss(next))
        {
            if (EnabledSet(EquipTriggerType.Bossing) is not null)
            {
                _inMovementSet = false;
                _log?.Info(EquipmentManager.LogCategory, "about to enter a boss room — wearing the Bossing set before the step");
                Fire(EquipTriggerType.Bossing);
            }
            return;
        }
        if (_inMovementSet
            && _isLair is { } lair && lair(next)
            && _readEquipment().SwapToDefaultBeforeLairs
            && MovementSetActive())
        {
            _inMovementSet = false;
            _log?.Info(EquipmentManager.LogCategory, "about to enter a lair — swapping to Default before the step");
            Fire(EquipTriggerType.Default);
        }
    }

    // The enabled, non-empty set for a trigger, or null. An empty set (no slots) is
    // treated as absent so it can't shadow a fallback — e.g. an empty movement set must
    // not suppress the loop-start Default.
    private EquipmentSet? EnabledSet(EquipTriggerType type)
    {
        EquipmentSet? set = _readEquipment().Sets.FirstOrDefault(s => s.Trigger == type);
        return set is { Enabled: true } && set.Slots.Count > 0 ? set : null;
    }

    // Whether the movement set takes over travel gear (enabled + has items).
    private bool MovementSetActive() => EnabledSet(EquipTriggerType.WhileMoving) is not null;

    private bool CurrentRoomIsBoss() =>
        _isBossRoom is { } probe && _currentRoom is { } r && probe(r);

    // Recovery just topped off to rest-max (a held rest gate cleared), fired from
    // HealthManager while the character is STILL resting in the room — before the
    // loop's deferred step-out. Swapping back to Default here (rather than on the
    // later stand, which IS the move) lets the swap complete in-room; the paced
    // apply holds the loop via the gear-swap movement gate, so we step out already
    // in Default gear instead of streaming the wears into the next room mid-combat
    // (report paradigm-20260826-140341). Gated on using rest swap sets (else there's
    // nothing to revert) and skipped in combat — a fight that interrupted recovery
    // is fought in the current loadout, per the rule.
    public void OnRecoveryComplete()
    {
        if (_player.InCombat || !UsingRestingSwapSets()) return;
        Fire(EquipTriggerType.Default);
    }

    private void OnPositionChanged(PlayerPosition from, PlayerPosition to)
    {
        if (from == to) return;
        if (ClassifyRest(to, _hpGateAsserted(), _maGateAsserted()) is { } restType)
            Fire(restType);
        else if (to == PlayerPosition.Standing && IsRestPosture(from))
        {
            // Only revert to Default when we're actually swapping BACK from a
            // pre-rest set — i.e. a pre-rest swap set is enabled. Without one the
            // rest never left Default (or the user hand-equipped some other set),
            // so a Default fire here would clobber that loadout for nothing.
            if (!UsingRestingSwapSets()) return;
            // Standing up mid-recovery is transient — a between-round cast, a loot
            // grab, or the pre-rest swap's own wear breaks rest, and the
            // HealthManager immediately re-issues it. Reverting to Default here
            // starts a swap→stand→swap thrash that re-arms the pre-rest set every
            // cycle and never lets the character actually recover (report
            // paradigm-20260826-132742). Only fall back to Default once recovery is
            // done — neither rest gate is still held, i.e. the pool has reached
            // rest-max (the gates clear at rest-target; see HealthManager).
            if (_hpGateAsserted() || _maGateAsserted())
            {
                _log?.Debug(EquipmentManager.LogCategory,
                    "auto-equip 'Default' held: still recovering (rest gate asserted) — "
                    + "not reverting the pre-rest set until the pool reaches rest-max");
                return;
            }
            // OnRecoveryComplete already swapped back to Default in-room, just before
            // this stand; don't fire a second, redundant Default swap on the stand-up
            // (report paradigm-20260827-125103). Only fires here when nothing recently
            // reverted Default — a stand OnRecoveryComplete didn't cover.
            if (_now() - _lastDefaultAppliedAt < DefaultReapplySuppressWindow)
            {
                _log?.Debug(EquipmentManager.LogCategory,
                    "auto-equip 'Default' held: recovery already reverted to Default in-room "
                    + "(suppressing the redundant stand-up swap)");
                return;
            }
            Fire(EquipTriggerType.Default);   // recovered — back to baseline
        }
    }

    // True when a pre-rest swap set (HP or Mana) is enabled — i.e. resting swaps
    // the loadout, so finishing a rest should swap it back. Read live off the
    // config so enabling / disabling a set takes effect without a restart.
    private bool UsingRestingSwapSets()
    {
        EquipmentSettings cfg = _readEquipment();
        return cfg.Sets.Any(s => s.Enabled
            && (s.Trigger == EquipTriggerType.PreRestHp || s.Trigger == EquipTriggerType.PreRestMana));
    }

    private static bool IsRestPosture(PlayerPosition p) =>
        p is PlayerPosition.Resting or PlayerPosition.Meditating;

    private void Fire(EquipTriggerType type)
    {
        // Respect the automation master switch — with the Auto-All kill-switch
        // engaged the user has silenced every engine, so a posture transition or
        // loop start must not auto-swap gear. Explicit applies (Workshop "Apply
        // Now", "Equip All", @equip-<set>) don't flow through here, so they still
        // work with the kill-switch on.
        if (!_isAutoEnabled()) return;
        // An item-cast buff swap just borrowed an equip slot and restores it itself
        // (see _lastItemCastSwapAt) — often the very rest-break that swap caused is
        // what fired this. Hold so the sequencer's own restore isn't doubled.
        if (_now() - _lastItemCastSwapAt < ItemCastSwapSuppressWindow)
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: item-cast swap in progress (its own restore owns the slot)");
            return;
        }
        if (ResolveTarget(_readEquipment(), type) is not { } setId) return;
        // Hold the fire until a full 'i' has established the worn set. Diffing a
        // set against an empty (never-parsed) loadout treats every item as unworn
        // and emits redundant `wear`s — e.g. re-wielding the weapon already held.
        if (!_wornLoadoutKnown())
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: worn loadout unknown (no inventory dump yet)");
            return;
        }
        if (_applyBySetId(setId) == EquipResult.Applied)
        {
            // Stamp a real Default swap so the stand-up that follows a recovery-
            // complete revert doesn't fire a second one (report -125103).
            if (type == EquipTriggerType.Default) _lastDefaultAppliedAt = _now();
            _log?.Info(EquipmentManager.LogCategory, $"auto-equip '{type}' applied its set");
        }
    }

    public void Dispose() => _player.PropertyChanged -= OnPlayerChanged;
}
