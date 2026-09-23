using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The Classes tab's ExpTable column shows the class exp MODIFIER (raw MDB delta + 100, the
// 100% baseline), matching the game / MMUD-GreaterMUD Explorer — e.g. Warrior 320 -> 420%,
// Thief 230 -> 330%, Paladin 490 -> 590%. See ExperienceTableCalculator.CalcExpChart, which
// adds the same 100 to the class term.
public sealed class ClassesSectionViewModelTests
{
    [Theory]
    [InlineData("320", "420%")]
    [InlineData("230", "330%")]
    [InlineData("490", "590%")]
    [InlineData("0", "100%")]
    public void FormatExpModifier_AddsHundredBaseline_AndPercent(string raw, string expected)
        => Assert.Equal(expected, ClassesSectionViewModel.FormatExpModifier(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("n/a")]
    public void FormatExpModifier_PassesThroughNonNumeric(string? raw)
        => Assert.Equal(raw, ClassesSectionViewModel.FormatExpModifier(raw));
}
