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

        // Kill-switch engaged: entering combat must NOT auto-swap gear.
        player.InCombat = true;
        Assert.Empty(applied);

        // Re-enabled: a fresh combat transition now applies the Default set.
        player.InCombat = false;
        autoEnabled = true;
        player.InCombat = true;
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

        // Baseline: entering combat fires the Default set.
        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);

        // Item-cast swap just began — a fire inside the window is held.
        applied.Clear();
        player.InCombat = false;
        coord.NoteItemCastSwap();
        player.InCombat = true;
        Assert.Empty(applied);

        // Past the window, a fresh fire applies again.
        applied.Clear();
        player.InCombat = false;
        clock += TimeSpan.FromSeconds(5);
        player.InCombat = true;
        Assert.Equal(new[] { "default-set" }, applied);
    }
}
