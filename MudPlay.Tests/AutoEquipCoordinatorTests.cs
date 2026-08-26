using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins the pure decision halves of <see cref="AutoEquipCoordinator"/> — which
/// trigger a posture change implies (<see cref="AutoEquipCoordinator.ClassifyRest"/>)
/// and which set (if any) a moment should apply given the live config
/// (<see cref="AutoEquipCoordinator.ResolveTarget"/>). The subscription plumbing is
/// UI glue smoke-tested via the live app.
/// </summary>
public sealed class AutoEquipCoordinatorTests
{
    // ===== ClassifyRest =====

    [Fact]
    public void ClassifyRest_Meditating_IsPreRestManaRegardlessOfGates()
    {
        Assert.Equal(EquipTriggerType.PreRestMana,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Meditating, hpGate: true, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithHpGate_IsPreRestHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: true, maGate: false));
    }

    [Fact]
    public void ClassifyRest_RestingWithOnlyMaGate_IsPreRestMana()
    {
        Assert.Equal(EquipTriggerType.PreRestMana,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: false, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithBothGates_PrefersHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: true, maGate: true));
    }

    [Fact]
    public void ClassifyRest_RestingWithNoGate_DefaultsToHp()
    {
        Assert.Equal(EquipTriggerType.PreRestHp,
            AutoEquipCoordinator.ClassifyRest(PlayerPosition.Resting, hpGate: false, maGate: false));
    }

    [Fact]
    public void ClassifyRest_Standing_IsNull()
    {
        Assert.Null(AutoEquipCoordinator.ClassifyRest(PlayerPosition.Standing, hpGate: true, maGate: true));
    }

    // ===== ResolveTarget =====

    private static EquipmentSettings Config(params EquipmentSet[] sets)
        => new() { Sets = sets.ToList() };

    private static EquipmentSet SetFor(EquipTriggerType trigger, bool enabled, string id)
        => new() { Trigger = trigger, Enabled = enabled, Id = id };

    [Fact]
    public void ResolveTarget_NoSetForType_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Backstab, enabled: true, "set-1"));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_SetDisabled_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: false, "set-1"));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_EnabledButNoId_ReturnsNull()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, ""));

        Assert.Null(AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_EnabledWithId_ReturnsSetId()
    {
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "set-42"));

        Assert.Equal("set-42", AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.Default));
    }

    [Fact]
    public void ResolveTarget_PicksTheSetForTheRequestedTrigger()
    {
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"),
            SetFor(EquipTriggerType.PreRestMana, enabled: true, "mana-set"));

        Assert.Equal("mana-set", AutoEquipCoordinator.ResolveTarget(cfg, EquipTriggerType.PreRestMana));
    }

    // ===== master gate (Auto-All kill-switch) =====

    [Fact]
    public void Fire_Suppressed_WhenAutoDisabled_ThenApplies_OnceReEnabled()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new System.Collections.Generic.List<string>();
        bool autoEnabled = false;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => autoEnabled);

        // Kill-switch engaged: a loop start must NOT auto-swap gear.
        coord.OnLoopStarted();
        Assert.Empty(applied);

        // Re-enabled: a fresh loop start now applies the Default set.
        autoEnabled = true;
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // ===== item-cast swap suppression =====

    // An item-cast buff temporarily borrows an equip slot and restores it itself;
    // an auto-equip fire it triggers (e.g. the rest it breaks) within the window must
    // be HELD so the restore isn't doubled (report paradigm-20260815-130733).
    [Fact]
    public void Fire_HeldBrieflyAfterItemCastSwap_ThenResumes()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();
        DateTimeOffset clock = DateTimeOffset.UnixEpoch;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true,
            log: null,
            now: () => clock);

        // Baseline: a loop start fires the Default set.
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);

        // Item-cast swap just began — a fire inside the window is held.
        applied.Clear();
        coord.NoteItemCastSwap();
        coord.OnLoopStarted();
        Assert.Empty(applied);

        // Past the window, a fresh fire applies again.
        applied.Clear();
        clock += TimeSpan.FromSeconds(5);
        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // ===== recovery-gate hold on stand-up (report paradigm-20260826-132742) =====

    // While a rest-if-below cycle is active (a recovery gate still held), standing
    // up is transient — the HealthManager re-rests. Reverting to Default here would
    // thrash Default↔pre-rest every cycle, so the fall-back is held until recovery
    // completes (the gates clear at rest-max).
    [Fact]
    public void StandFromRest_WhileRecoveryGateHeld_HoldsDefault_ThenFiresOnceCleared()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();
        bool hpGate = true;

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => hpGate,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        // Rest-if-below fires: swap to the HP set.
        player.Position = PlayerPosition.Resting;
        Assert.Equal(new[] { "hp-set" }, applied);

        // A between-round cast / loot grab breaks rest → Standing, but the gate is
        // still held (still below rest-max): must NOT revert to Default.
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        Assert.Empty(applied);

        // Recovery completes: pool hit rest-max, the gate clears; the next stand-up
        // now swaps back to the Default set.
        player.Position = PlayerPosition.Resting;   // HealthManager re-rests (still gated)
        hpGate = false;
        applied.Clear();
        player.Position = PlayerPosition.Standing;
        Assert.Equal(new[] { "default-set" }, applied);
    }

    // Combat entry no longer auto-swaps to Default — a fight interrupting a rest
    // leaves the pre-rest loadout on until recovery completes (per the user's rule).
    [Fact]
    public void CombatEntry_DoesNotFireDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.InCombat = true;
        Assert.Empty(applied);
    }

    // No pre-rest swap set enabled ⇒ resting never left Default (or the user
    // hand-equipped another set), so finishing a rest must NOT fire Default.
    [Fact]
    public void StandFromRest_NotUsingRestSwapSets_DoesNotFireDefault()
    {
        var player = new PlayerState { Position = PlayerPosition.Standing };
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,   // fully recovered
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        player.Position = PlayerPosition.Resting;   // no pre-rest set to fire
        player.Position = PlayerPosition.Standing;
        Assert.Empty(applied);
    }

    // Recovery topping off to rest-max swaps back to Default (fired while still
    // resting, before the loop steps out) when a pre-rest set is in use and we're
    // not in combat.
    [Fact]
    public void OnRecoveryComplete_UsingRestSets_NotInCombat_FiresDefault()
    {
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Equal(new[] { "default-set" }, applied);
    }

    [Fact]
    public void OnRecoveryComplete_InCombat_DoesNotFire()
    {
        var player = new PlayerState { InCombat = true };
        EquipmentSettings cfg = Config(
            SetFor(EquipTriggerType.Default, enabled: true, "default-set"),
            SetFor(EquipTriggerType.PreRestHp, enabled: true, "hp-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Empty(applied);
    }

    [Fact]
    public void OnRecoveryComplete_NotUsingRestSets_DoesNotFire()
    {
        var player = new PlayerState { InCombat = false };
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnRecoveryComplete();
        Assert.Empty(applied);
    }

    // A loop / Auto-Lair run beginning swaps to the Default set.
    [Fact]
    public void OnLoopStarted_FiresDefault()
    {
        var player = new PlayerState();
        EquipmentSettings cfg = Config(SetFor(EquipTriggerType.Default, enabled: true, "default-set"));
        var applied = new List<string>();

        using var coord = new AutoEquipCoordinator(
            player,
            readEquipment: () => cfg,
            hpGateAsserted: () => false,
            maGateAsserted: () => false,
            applyBySetId: id => { applied.Add(id); return EquipResult.Applied; },
            wornLoadoutKnown: () => true,
            isAutoEnabled: () => true);

        coord.OnLoopStarted();
        Assert.Equal(new[] { "default-set" }, applied);
    }
}
