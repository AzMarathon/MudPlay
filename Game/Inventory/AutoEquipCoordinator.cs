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
//     begins (OnLoopStarted). Combat entry deliberately does NOT swap to Default
//     — if a fight interrupts a rest-if-below the character keeps its pre-rest
//     loadout and only reverts once recovered, per the user's rule (report
//     paradigm-20260826-132742). The remaining Default path — re-wearing on death-
//     pile recovery — lives in the recovery engine, gated by its own setting.
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
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    private PlayerPosition _lastPosition;

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

    public AutoEquipCoordinator(
        PlayerState player,
        Func<EquipmentSettings> readEquipment,
        Func<bool> hpGateAsserted,
        Func<bool> maGateAsserted,
        Func<string, EquipResult> applyBySetId,
        Func<bool> wornLoadoutKnown,
        Func<bool> isAutoEnabled,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
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
        if (e.PropertyName != nameof(PlayerState.Position)) return;
        OnPositionChanged(_lastPosition, _player.Position);
        _lastPosition = _player.Position;
    }

    // A loop or Auto-Lair run just began — swap to the Default (baseline) set.
    // Wired from AppServices to LoopRunner's ReachedFirstWaypoint and AutoLair's
    // ActiveChanged(true). Unconditional (subject only to Fire's own gates): a run
    // starting means we're moving out under normal combat gear, whatever we were
    // wearing while idle / resting beforehand.
    public void OnLoopStarted() => Fire(EquipTriggerType.Default);

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
            _log?.Info(EquipmentManager.LogCategory, $"auto-equip '{type}' applied its set");
    }

    public void Dispose() => _player.PropertyChanged -= OnPlayerChanged;
}
