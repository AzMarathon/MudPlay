namespace MudPlay.Game.Calculators;

// One column of a base stat's breakpoint table: the sub-stat it feeds, the formula
// for that stat's own share of it, and every stat value where that share changes.
// Approximate marks a term the game divides together with other stats, so the real
// step can land a point either side. StockFormulaOnParadigm marks a term only
// confirmed on Stock, shown on Paradigm with Stock's formula. Unit is appended to
// each value ("%" for prices); Signed puts a + on positive values.
public sealed record StatBreakpointColumn(
    string Label, string Formula, IReadOnlyList<StatBreakpointColumn.Step> Steps,
    bool Approximate = false, bool StockFormulaOnParadigm = false,
    string Unit = "", bool Signed = true)
{
    public readonly record struct Step(int Stat, int Value);

    // Index of the step a stat value falls in (the last step at or below it); -1 when
    // the value is below the first step.
    public int StepIndexFor(int stat)
    {
        int found = -1;
        for (int i = 0; i < Steps.Count && Steps[i].Stat <= stat; i++) found = i;
        return found;
    }

    public string Format(int value) =>
        (Signed && value > 0 ? "+" : "") + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + Unit;
}
