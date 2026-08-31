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
        {
            (string text, _) = NavActivity.Describe(
                new[] { gate }, isPaused: true, isMovementPrevented: false);
            Assert.NotEqual($"Waiting — {gate}", text);   // raw-name fallback = unmapped
            Assert.NotEmpty(text);
        }
    }

    [Fact]
    public void AutoAll_ReadsAsAutoEnginesOff()
    {
        (string text, NavActivityKind kind) = NavActivity.Describe(
            new[] { MovementCoordinator.AutoAllGate }, isPaused: true, isMovementPrevented: false);
        Assert.Equal(NavActivityKind.Waiting, kind);
        Assert.Contains("Auto-All", text);
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
        Assert.Equal("Waiting — held", text);
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
}
