using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The Add/Edit buff dialog's mana-regen reroll threshold: both realms edit the rolled
// value in a number box spanning the roll spell's level-scaled range (negatives
// included); Stock adds the tick steps, since its roll is read back off the tick.
public sealed class AddBuffDialogViewModelTests
{
    private static readonly BuffPickOption[] Picks = { new("flux", "mana flux (Lvl 16)", true) };

    private static AddBuffDialogViewModel Dialog(bool stock, string? steps = null,
        (int Min, int Max)? roll = null) =>
        new(Picks, isLightSpell: _ => false, isRollSpell: s => s == "flux",
            isStockRealm: stock, tickSteps: _ => steps,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 3, null),
            rollRange: _ => roll);

    [Fact]
    public void Stock_UsesTheRollBox_AndShowsTheTickSteps()
    {
        AddBuffDialogViewModel d = Dialog(stock: true, steps: "6 MP/tick at worst · 7 from 12", roll: (-64, 216));

        Assert.True(d.ShowRerollNumeric);
        Assert.Equal(-64m, d.RerollNumericMinimum);
        Assert.Equal("6 MP/tick at worst · 7 from 12", d.RerollTickStepsText);
        d.RerollThreshold = 75;
        Assert.Equal(75, d.RerollThreshold);
    }

    [Fact]
    public void Paradigm_NumberBox_SpansTheLevelScaledRoll_IncludingNegatives()
    {
        AddBuffDialogViewModel d = Dialog(stock: false, roll: (-64, 216));

        Assert.Equal(-64m, d.RerollNumericMinimum);
        Assert.Equal(216m, d.RerollNumericMaximum);
        Assert.Equal("rolls -64 … 216 at your level", d.RerollRollRangeText);
    }

    [Fact]
    public void Paradigm_UnknownRange_AllowsNegatives()
    {
        AddBuffDialogViewModel d = Dialog(stock: false, roll: null);

        Assert.Equal(-999m, d.RerollNumericMinimum);
        Assert.Equal(999m, d.RerollNumericMaximum);
    }
}
