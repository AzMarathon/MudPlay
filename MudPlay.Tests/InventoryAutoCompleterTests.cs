using System;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins InventoryAutoCompleter's word-completion + cycle tracking: matching
// spans carried/equipped/keys, repeat presses on an unchanged line continue the
// cycle (Next forward, Previous backward, wrapping both ways), any other edit
// to the line starts a fresh match, and the Enabled gate + empty-prefix case are
// both silent no-ops.
public sealed class InventoryAutoCompleterTests
{
    private static InventorySnapshot Snap(
        string[]? carried = null, EquippedItem[]? equipped = null, string[]? keys = null)
        => new(
            CurrencyHoldings.Empty,
            EncumbranceReading.Empty,
            equipped ?? Array.Empty<EquippedItem>(),
            carried ?? Array.Empty<string>(),
            DateTimeOffset.UtcNow,
            null,
            keys);

    [Fact]
    public void Next_NoMatch_ReturnsNull()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a torch" });
        Assert.Null(ac.Next("drop emerald", snap));
    }

    [Fact]
    public void Next_SingleMatch_CompletesImmediately()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "an emerald-hilted rapier" });
        Assert.Equal("drop emerald-hilted", ac.Next("drop emerald-", snap));
    }

    [Fact]
    public void Next_CyclesThroughAllCandidates_ThenWrapsToFirst()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = ac.Next("get r", snap);
        Assert.Equal("get rusty", first);

        string? second = ac.Next(first!, snap);
        Assert.Equal("get rope", second);

        string? third = ac.Next(second!, snap);
        Assert.Equal("get rusty", third); // wrapped back to the first candidate
    }

    [Fact]
    public void Previous_CyclesBackward_WrappingToTheLastCandidate()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = ac.Next("get r", snap);   // -> "get rusty" (index 0)
        string? back = ac.Previous(first!, snap); // step back from index 0 -> wraps to "rope"
        Assert.Equal("get rope", back);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a Rusty Dagger" });
        Assert.Equal("get Rusty", ac.Next("get r", snap));
    }

    [Fact]
    public void Matching_SpansCarriedEquippedAndKeys_InThatOrder()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(
            carried: new[] { "a torch" },
            equipped: new[] { new EquippedItem("a keen longsword", "Weapon") },
            keys: new[] { "a keyring key" });

        string? first = ac.Next("wield k", snap);
        Assert.Equal("wield keen", first);       // equipped, since no carried item starts with "k"

        string? second = ac.Next(first!, snap);
        Assert.Equal("wield keyring", second);   // then the key-ring

        string? third = ac.Next(second!, snap);
        Assert.Equal("wield key", third);
    }

    [Fact]
    public void Disabled_AlwaysReturnsNull()
    {
        InventoryAutoCompleter ac = new() { Enabled = false };
        InventorySnapshot snap = Snap(carried: new[] { "a torch" });
        Assert.Null(ac.Next("get t", snap));
    }

    [Fact]
    public void EmptyTrailingWord_IsNoOp()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a torch" });
        Assert.Null(ac.Next("get ", snap));
        Assert.Null(ac.Next("", snap));
    }

    [Fact]
    public void EditingTheLineBetweenPresses_StartsAFreshMatch()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = ac.Next("get r", snap);
        Assert.Equal("get rusty", first);

        // The user kept typing instead of pressing Tab again — the next Tab
        // press must match "getgo", not continue the old "r" cycle.
        Assert.Null(ac.Next("getgo", snap));
    }
}
