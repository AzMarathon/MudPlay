using System.Text;
using MudPlay.Game;
using MudPlay.Game.Train;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Reading the `train stats` form off the terminal, and deciding from it whether a
// plan row was applied (report paradigm-20260930-160602: the form showed
// Strength 133 and Agility 100 with CP Left 26 while `stat` read 153 and 90).
public sealed class TrainStatsScreenTests
{
    private static readonly string[] Labels = { "Strength", "Intellect", "Willpower", "Agility", "Health", "Charm" };
    private static readonly string[] Ranges =
    {
        "(  55 to  160)", "(  30 to  130)", "(  30 to  135)", "(  45 to  145)", "(  55 to  165)", "(  30 to  120)",
    };

    // Paints the form the way Paradigm does, with cursor positioning, at the rows
    // and columns of the capture: labels at column 7 of rows 9-14, the ranges at
    // column 18, the values right-aligned in four cells from column 34, and CP Left
    // on row 20.
    private static string Paint(int[] stats, int cpLeft)
    {
        var emulator = new TerminalEmulator(80, 25);
        var wire = new StringBuilder("\x1b[2J\x1b[1;1H");
        wire.Append("\x1b[2;4H/ P A R A D I G M      Char. Creation /    \\  --    Point Cost Chart    --");
        for (int i = 0; i < Labels.Length; i++)
        {
            int row = 9 + i;
            wire.Append($"\x1b[{row};3H| > {Labels[i]}\x1b[{row};39H<\x1b[{row};41H|");
            wire.Append($"\x1b[{row};18H\x1b[0m{Ranges[i]}");
            wire.Append($"\x1b[{row};34H\x1b[1;37m\x1b[40m{stats[i],4}\x1b[0m");
        }
        wire.Append("\x1b[20;3H| >  Exit:\x1b[20;19H<\x1b[20;22H> CP Left:\x1b[20;39H<\x1b[20;41H|");
        wire.Append("\x1b[20;14H\x1b[1;37m\x1b[40mSAVE\x1b[0m");
        wire.Append($"\x1b[20;33H\x1b[0;37m\x1b[40m{cpLeft,4}\x1b[0m");
        emulator.Feed(Encoding.Latin1.GetBytes(wire.ToString()));

        var text = new StringBuilder();
        for (int y = 0; y < emulator.Screen.Rows; y++)
        {
            foreach (Cell cell in emulator.Screen.Row(y)) text.Append(cell.Char);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static TrainStatsScreen Form(int str, int cpLeft) =>
        new(new[] { str, 40, 30, 100, 60, 30 }, cpLeft);

    [Fact]
    public void ReadsTheSixStatsAndCpLeft_OffTheCursorPaintedForm()
    {
        TrainStatsScreen? form = TrainStatsScreen.TryRead(Paint(new[] { 133, 40, 30, 100, 60, 30 }, 26));

        Assert.NotNull(form);
        Assert.Equal(new[] { 133, 40, 30, 100, 60, 30 }, form.Stats);
        Assert.Equal(26, form.CpLeft);
    }

    [Fact]
    public void AScreenWithoutTheForm_IsNotRead()
    {
        Assert.Null(TrainStatsScreen.TryRead(null));
        Assert.Null(TrainStatsScreen.TryRead("Obvious exits: north\nStrength:  153    Agility: 90\n"));
    }

    [Fact]
    public void AHalfDrawnForm_IsNotReadInPart()
    {
        string full = Paint(new[] { 133, 40, 30, 100, 60, 30 }, 26);

        Assert.Null(TrainStatsScreen.TryRead(full.Replace("Charm", "     ")));
        Assert.Null(TrainStatsScreen.TryRead(full.Replace("CP Left:", "        ")));
    }

    // ----- was the row applied? ----------------------------------------------

    private static readonly int[] Row = { 136, 40, 30, 100, 60, 30 };

    [Fact]
    public void FormReachedTheRow_IsApplied()
    {
        Assert.Equal(CpApplyOutcome.Applied, CpApplyCheck.FromForm(Form(133, 26), Form(136, 1), Row));
    }

    [Fact]
    public void FormUnchanged_NothingWasSpent_TheRowIsNotApplied()
    {
        // The incident: keystrokes went out, the form took none, CP Left stayed 26.
        Assert.Equal(CpApplyOutcome.NothingSpent, CpApplyCheck.FromForm(Form(133, 26), Form(133, 26), Row));
    }

    [Fact]
    public void FormPartWay_IsPartial()
    {
        Assert.Equal(CpApplyOutcome.Partial, CpApplyCheck.FromForm(Form(133, 26), Form(135, 10), Row));
    }

    [Fact]
    public void FormAlreadyAtTheRow_IsApplied_WithNothingSpent()
    {
        // Trained by hand earlier: the form itself shows the row is done.
        Assert.Equal(CpApplyOutcome.Applied, CpApplyCheck.FromForm(Form(136, 1), Form(136, 1), Row));
    }

    [Theory]
    [InlineData(26, 1, 25, true, CpApplyOutcome.Applied)]        // exactly the row's cost left
    [InlineData(26, 26, 25, true, CpApplyOutcome.NothingSpent)]  // the incident, seen through `stat`
    [InlineData(26, 10, 25, true, CpApplyOutcome.Partial)]       // some CP gone, not the row's cost
    [InlineData(26, 1, 25, false, CpApplyOutcome.Partial)]       // only the affordable part was typed
    public void WithoutTheForm_TheCpEitherSideDecides(int before, int after, int cost, bool wholeRow, CpApplyOutcome expected)
    {
        Assert.Equal(expected, CpApplyCheck.FromStat(before, after, cost, wholeRow));
    }
}
