using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The "Override Attack" box disambiguation (<see cref="MonsterEditDialogViewModel.ParseAttackOverride"/>):
/// a positive integer is a Spell.Number (routed through the mana-gated attack-spell
/// rung); any other non-empty text is a raw command / cast-code sent as-is; blank is
/// no override. This is what lets "attack" persist (report paradigm-20260809-131642 —
/// it used to be silently dropped by an int-only parse).
/// </summary>
public sealed class MonsterEditDialogViewModelTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("  42  ")]   // trimmed
    public void ParseAttackOverride_PositiveInteger_IsSpellId(string text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Equal(42, spellId);
        Assert.Null(command);
    }

    [Theory]
    [InlineData("attack", "attack")]
    [InlineData("  harm  ", "harm")]   // trimmed, kept as a command
    [InlineData("bash", "bash")]
    [InlineData("0", "0")]             // non-positive int is not a spell id → command
    [InlineData("-3", "-3")]
    public void ParseAttackOverride_NonNumericText_IsCommand(string text, string expected)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAttackOverride_Blank_IsNoOverride(string? text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_SetsExactlyOneOfThePair()
    {
        // The two backing fields are mutually exclusive — a species never carries
        // both a spell id and a command.
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride("attack");
        Assert.True(spellId is null ^ command is null || (spellId is null && command is null));
        Assert.Null(spellId);
        Assert.NotNull(command);
    }
}
