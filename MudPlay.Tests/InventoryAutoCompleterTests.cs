using System;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins InventoryAutoCompleter's completion + cycle tracking: a match completes
// to the REST of the item's name (not just the matched word — completing a word
// to itself would silently leave the line unchanged, see
// AlreadyTypedWordCompletesToTheRestOfTheItemName below), matching spans
// carried/equipped/keys, repeat presses on an unchanged (text, caret) pair
// continue the cycle (Next forward, Previous backward, wrapping both ways),
// any edit OR caret move between presses starts a fresh match, and the
// caret-aware completion only ever touches the word immediately before the
// caret — required for the Conversation window's TextBox, which (unlike the
// terminal's append-only LocalInputBuffer) supports a mid-line cursor.
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

    // Most tests exercise the terminal's shape of call — caret pinned to the
    // end of the line — where the trailing word is always what's completed.
    private static string? NextAtEnd(InventoryAutoCompleter ac, string text, InventorySnapshot snap)
        => ac.Next(text, text.Length, snap)?.Text;

    private static string? PreviousAtEnd(InventoryAutoCompleter ac, string text, InventorySnapshot snap)
        => ac.Previous(text, text.Length, snap)?.Text;

    [Fact]
    public void Next_NoMatch_ReturnsNull()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a torch" });
        Assert.Null(NextAtEnd(ac, "drop emerald", snap));
    }

    [Fact]
    public void Next_SingleMatch_CompletesToTheRestOfTheItemName()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "an emerald-hilted rapier" });
        Assert.Equal("drop emerald-hilted rapier", NextAtEnd(ac, "drop emerald-", snap));
    }

    // Regression for paradigm-20260911-231808: typing the matched word out in
    // full ("holy" exactly matches the word "holy" in "holy medallion") must
    // still complete forward to "holy medallion" — completing "holy" to itself
    // would leave the buffer text unchanged and Tab would look like a no-op,
    // which is exactly what was reported.
    [Fact]
    public void AlreadyTypedWordCompletesToTheRestOfTheItemName()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "holy medallion" });
        Assert.Equal("drop holy medallion", NextAtEnd(ac, "drop holy", snap));
    }

    [Fact]
    public void Next_CyclesThroughAllCandidates_ThenWrapsToFirst()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = NextAtEnd(ac, "get r", snap);
        Assert.Equal("get rusty dagger", first);

        string? second = NextAtEnd(ac, first!, snap);
        Assert.Equal("get rope", second);

        string? third = NextAtEnd(ac, second!, snap);
        Assert.Equal("get rusty dagger", third); // wrapped back to the first candidate
    }

    [Fact]
    public void Previous_CyclesBackward_WrappingToTheLastCandidate()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = NextAtEnd(ac, "get r", snap);       // -> "get rusty dagger" (index 0)
        string? back = PreviousAtEnd(ac, first!, snap);     // step back from index 0 -> wraps to "rope"
        Assert.Equal("get rope", back);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a Rusty Dagger" });
        Assert.Equal("get Rusty Dagger", NextAtEnd(ac, "get r", snap));
    }

    [Fact]
    public void Matching_SpansCarriedEquippedAndKeys_InThatOrder()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(
            carried: new[] { "a torch" },
            equipped: new[] { new EquippedItem("a keen longsword", "Weapon") },
            keys: new[] { "a keyring key" });

        string? first = NextAtEnd(ac, "wield k", snap);
        Assert.Equal("wield keen longsword", first); // equipped, since no carried item starts with "k"

        string? second = NextAtEnd(ac, first!, snap);
        Assert.Equal("wield keyring key", second);   // then the key-ring, by its leading word "keyring"

        string? third = NextAtEnd(ac, second!, snap);
        Assert.Equal("wield keen longsword", third); // wraps back to the first candidate
    }

    // Regression: typing a short prefix must not surface an item via a word
    // BURIED in its name ("emblem", the second word of "bronze emblem")
    // ahead of — or instead of — items that actually start with that prefix.
    // Only the leading word (skipping "a"/"an") is checked; reaching "bronze
    // emblem" takes typing "bro" or "bronze".
    [Fact]
    public void OnlyMatchesTheItemsLeadingWord_NotAWordBuriedInTheName()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "bronze emblem", "emerald-tipped crozier" });

        Assert.Equal("drop emerald-tipped crozier", NextAtEnd(ac, "drop e", snap));
        Assert.Null(NextAtEnd(ac, "drop emb", snap));       // "emblem" is not a leading word anywhere
        Assert.Equal("drop bronze emblem", NextAtEnd(ac, "drop bro", snap));
    }

    [Fact]
    public void EmptyTrailingWord_IsNoOp()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a torch" });
        Assert.Null(NextAtEnd(ac, "get ", snap));
        Assert.Null(NextAtEnd(ac, "", snap));
    }

    [Fact]
    public void EditingTheLineBetweenPresses_StartsAFreshMatch()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a rusty dagger", "a rope" });

        string? first = NextAtEnd(ac, "get r", snap);
        Assert.Equal("get rusty dagger", first);

        // The user kept typing instead of pressing Tab again — the next Tab
        // press must match "getgo", not continue the old "r" cycle.
        Assert.Null(NextAtEnd(ac, "getgo", snap));
    }

    // Required for the Conversation window's TextBox: with the caret in the
    // MIDDLE of the line, only the word immediately before it is completed —
    // everything after the caret is preserved untouched, not clobbered by
    // whatever the trailing word of the whole line happens to be.
    [Fact]
    public void CompletesTheWordAtTheCaret_NotTheTrailingWord()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "holy medallion" });
        const string text = "wear hol ring"; // caret placed right after "hol"
        int caret = "wear hol".Length;

        InventoryAutoCompleter.Completion? result = ac.Next(text, caret, snap);

        Assert.NotNull(result);
        Assert.Equal("wear holy medallion ring", result!.Value.Text);
        Assert.Equal("wear holy medallion".Length, result.Value.CaretIndex);
    }

    // Same (text, caret) pair repeats the cycle; the SAME text with the caret
    // moved elsewhere (a click, or an arrow key) must NOT be read as a repeat
    // press — it starts a fresh match against whatever word the caret now
    // sits on.
    [Fact]
    public void MovingTheCaretWithoutChangingText_StartsAFreshMatch()
    {
        InventoryAutoCompleter ac = new();
        InventorySnapshot snap = Snap(carried: new[] { "a ring", "a rope" });
        const string text = "wear ri give ro";
        int caretAfterRi = "wear ri".Length;

        InventoryAutoCompleter.Completion first = ac.Next(text, caretAfterRi, snap)!.Value;
        Assert.Equal("wear ring give ro", first.Text);

        // Same text as `first` produced, but the caret is now after "ro" —
        // a fresh match against "ro", not a continuation of the "ri" cycle.
        int caretAfterRo = first.Text.Length;
        InventoryAutoCompleter.Completion? second = ac.Next(first.Text, caretAfterRo, snap);

        Assert.NotNull(second);
        Assert.Equal("wear ring give rope", second!.Value.Text);
    }
}
