using System.Globalization;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One sub-stat's column in the Stat Breakpoints window, with the character's row
// picked out and a one-line "you get" summary for the header.
public sealed class StatBreakpointColumnViewModel
{
    public string Label { get; }
    public string Formula { get; }
    public bool IsApproximate { get; }
    public bool IsStockFormula { get; }
    public string YouText { get; }
    public IReadOnlyList<StatBreakpointRowViewModel> Rows { get; }
    public int CurrentIndex { get; }

    public StatBreakpointColumnViewModel(StatBreakpointColumn column, int statValue)
    {
        Label = (column.Approximate ? "≈ " : "") + column.Label;
        Formula = column.Formula;
        IsApproximate = column.Approximate;
        IsStockFormula = column.StockFormulaOnParadigm;
        CurrentIndex = column.StepIndexFor(statValue);
        Rows = column.Steps
            .Select((s, i) => new StatBreakpointRowViewModel(
                s.Stat.ToString(CultureInfo.InvariantCulture), column.Format(s.Value), i == CurrentIndex, s.Value == 0))
            .ToList();
        YouText = CurrentIndex >= 0 ? $"You: {column.Format(column.Steps[CurrentIndex].Value)}" : "You: below the table";
    }
}
