using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The Add/Edit buff dialog's mana-regen reroll threshold: the Paradigm number box spans
// the roll spell's level-scaled range (negatives included), and the hidden Stock slider
// can't overwrite what was typed into the box.
public sealed class AddBuffDialogViewModelTests
{
    private static readonly BuffPickOption[] Picks = { new("flux", "mana flux (Lvl 16)", true) };

    private static AddBuffDialogViewModel Dialog(bool stock, (int Worst, int Best)? tick = null,
        (int Min, int Max)? roll = null) =>
        new(Picks, isLightSpell: _ => false, isRollSpell: s => s == "flux",
            isStockRealm: stock, tickRange: _ => tick,
            initial: new AddBuffResult("flux", 15, false, false, false, false, 3, null),
            rollRange: _ => roll);

    [Fact]
    public void HiddenSlider_CannotClampTheTypedThreshold()
    {
        // report paradigm-20260926-112808: with no tick range the hidden slider's
        // Maximum is 1; its coerced value was written back over every typed threshold.
        AddBuffDialogViewModel d = Dialog(stock: false, roll: (-64, 216));
        Assert.False(d.ShowRerollSlider);

        d.RerollThreshold = 75;
        d.RerollThresholdSlider = 1;          // what the hidden, coerced slider pushes back

        Assert.Equal(75, d.RerollThreshold);
    }

    [Fact]
    public void VisibleSlider_StillDrivesTheThreshold()
    {
        AddBuffDialogViewModel d = Dialog(stock: true, tick: (4, 11));
        Assert.True(d.ShowRerollSlider);

        d.RerollThresholdSlider = 9;

        Assert.Equal(9, d.RerollThreshold);
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
