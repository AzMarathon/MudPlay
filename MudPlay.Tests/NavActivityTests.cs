using System.Reflection;
using MudPlay.Game.Map;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// Pins NavActivity — the gate → "what is the engine doing / why is it held" mapping
// behind the Navigation top bar. The completeness test is the important one: it
// fails the build if a NEW MovementCoordinator gate is added without giving it a
// plain-English label, which is exactly the regression that left a queued walk
// reading "Waiting — AutoAll".
public sealed class NavActivityTests
{
    // Every gate constant on MovementCoordinator, by its wire value.
    private static IEnumerable<string> AllGateValues() =>
        typeof(MovementCoordinator)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                     && f.Name.EndsWith("Gate", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryGate_HasAPlainLabel_NotTheRawName()
    {
        foreach (string gate in AllGateValues())
            Assert.True(NavActivity.IsMapped(gate), $"gate '{gate}' has no label");
    }

    [Fact]
    public void AutoAll_ReadsAsAutoEnginesOff()
    {
        (string text, NavActivityKind kind) = NavActivity.Describe(
            new[] { MovementCoordinator.AutoAllGate }, isPaused: true, isMovementPrevented: false);
        Assert.Equal(NavActivityKind.Waiting, kind);
        Assert.Contains("Auto-all is off", text);
    }

    [Fact]
    public void NothingGating_IsMoving()
    {
        (string text, NavActivityKind kind) = NavActivity.Describe(
            Array.Empty<string>(), isPaused: false, isMovementPrevented: false);
        Assert.Equal(NavActivityKind.Moving, kind);
        Assert.Equal("Moving", text);
    }

    [Fact]
    public void Held_OutranksGates_ViaConditionFlag()
    {
        // The held condition flag is authoritative even with an unrelated wait gate up.
        (string text, NavActivityKind kind) = NavActivity.Describe(
            new[] { MovementCoordinator.HealthRecoveryGate }, isPaused: true, isMovementPrevented: true);
        Assert.Equal(NavActivityKind.Waiting, kind);
        Assert.Equal("Waiting — Held", text);
    }

    [Theory]
    [InlineData("Waiting — resting (low HP)", NavActivityKind.Waiting, "resting (low HP)")]
    // Fighting / Paused are already shown by the state chip's word, so they don't
    // fold onto the line — only the Waiting detail (which the chip omits) does.
    [InlineData("Fighting", NavActivityKind.Fighting, null)]
    [InlineData("Paused", NavActivityKind.Paused, null)]
    [InlineData("Moving", NavActivityKind.Moving, null)]
    [InlineData("Moving — checking the dark", NavActivityKind.Moving, null)]
    public void HoldSuffix_FoldsOnlyTheWaitDetail(string text, NavActivityKind kind, string? expected)
        => Assert.Equal(expected, NavActivity.HoldSuffix(text, kind));

    [Fact]
    public void ActiveHolds_ListsEveryHold_ButNotPauseOrCombat()
    {
        var holds = NavActivity.ActiveHolds(
            new[] { MovementCoordinator.CombatGate, MovementCoordinator.UserGate,
                    MovementCoordinator.SneakSettleGate, MovementCoordinator.HealthRecoveryGate },
            isMovementPrevented: false);
        Assert.Equal(new[] { "Low HP" }, holds.Select(h => h.Label));   // sneaking isn't shown
    }

    [Fact]
    public void ActiveHolds_HeldFlagAndGate_ShowOnce()
    {
        var holds = NavActivity.ActiveHolds(
            new[] { MovementCoordinator.HeldGate }, isMovementPrevented: true);
        Assert.Equal(new[] { "Held" }, holds.Select(h => h.Label));
    }

    [Fact]
    public void HoldChips_LingerAfterTheHoldEnds_ThenGo()
    {
        List<Action> due = [];
        NavHoldChipStrip strip = new((action, _) => due.Add(action));
        strip.Update([("sneaking", NavChipTone.Wait)]);
        strip.Update([]);

        NavHoldChip chip = Assert.Single(strip.Chips);
        Assert.True(chip.IsCleared);
        due.ForEach(a => a());
        Assert.Empty(strip.Chips);
    }

    [Fact]
    public void HoldChips_RelitWhileFading_IsAFreshChipTheFadeCantRemove()
    {
        List<Action> due = [];
        NavHoldChipStrip strip = new((action, _) => due.Add(action));
        strip.Update([("looting", NavChipTone.Wait)]);
        strip.Update([]);
        strip.Update([("looting", NavChipTone.Wait)]);

        due.ForEach(a => a());
        NavHoldChip chip = Assert.Single(strip.Chips);
        Assert.False(chip.IsCleared);
    }

    // The party holds name who they're about, and a stealth stop says what it casts
    // (user, 2026-09-30).
    [Fact]
    public void ActiveHolds_NameTheMembers_AndTheCast()
    {
        var holds = NavActivity.ActiveHolds(
            new[] { MovementCoordinator.PartyWaitGate, MovementCoordinator.AllyDownGate,
                    MovementCoordinator.MemberDisconnectGate, MovementCoordinator.PartyInviteGate,
                    MovementCoordinator.SneakCastGate },
            isMovementPrevented: false,
            new NavHoldNames(new[] { "Bob" }, new[] { "Ann" }, new[] { "Cy" }, new[] { "Dee" }, "Healing"));
        Assert.Equal(new[] { "@Wait Bob", "Downed Ally Dee", "Ann disconnected", "Waiting on Cy to join", "Healing" },
            holds.Select(h => h.Label));
    }

    // A pause the user's own typed move caused says so: nobody pressed Pause, and the
    // state chip alone reads "Paused" (report paradigm-20261003-162514).
    [Fact]
    public void ActiveHolds_NameTheTypedMoveThatPausedNavigation()
    {
        var holds = NavActivity.ActiveHolds(
            new[] { MovementCoordinator.UserGate },
            isMovementPrevented: false,
            NavHoldNames.None with { TypedMove = "u" });
        Assert.Equal(new[] { "You typed 'u' - Resume to go on" }, holds.Select(h => h.Label));

        // A pause from the Pause button has no chip of its own.
        Assert.Empty(NavActivity.ActiveHolds(new[] { MovementCoordinator.UserGate }, isMovementPrevented: false));
    }

    // The holds the user doesn't want to see get no chip.
    [Fact]
    public void ActiveHolds_HiddenHoldsAndSettleBeats_GetNoChip()
    {
        var holds = NavActivity.ActiveHolds(
            new[] { MovementCoordinator.AcquisitionGate, MovementCoordinator.SneakSettleGate,
                    MovementCoordinator.DarkRoomSettleGate, MovementCoordinator.GearSwapGate },
            isMovementPrevented: false);
        Assert.Empty(holds);
    }
}
