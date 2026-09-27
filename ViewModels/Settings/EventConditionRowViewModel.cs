using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One editable condition row in the event editor's "When" trigger: stat, comparison,
// value, and (for money) the denomination the value is counted in.
public sealed partial class EventConditionRowViewModel : ObservableObject
{
    private static readonly (EventConditionStat Stat, string Label)[] StatLabels =
    {
        (EventConditionStat.Money, "Money"),
        (EventConditionStat.Encumbrance, "Encumbrance %"),
        (EventConditionStat.Experience, "Experience"),
        (EventConditionStat.Level, "Level"),
    };

    private static readonly EventComparison[] ComparisonOrder =
    {
        EventComparison.AtLeast, EventComparison.AtMost, EventComparison.Above,
        EventComparison.Below, EventComparison.Equal, EventComparison.NotEqual,
    };

    private readonly Action<EventConditionRowViewModel> _remove;

    public IReadOnlyList<string> StatOptions { get; } = StatLabels.Select(static s => s.Label).ToArray();
    public IReadOnlyList<string> ComparisonOptions { get; } =
        ComparisonOrder.Select(Game.Events.EventConditionEvaluator.Symbol).ToArray();
    public IReadOnlyList<CoinDenomination> DenominationOptions { get; } =
        (CoinDenomination[])Enum.GetValues(typeof(CoinDenomination));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoney))]
    private string _selectedStat;

    [ObservableProperty] private string _selectedComparison;
    [ObservableProperty] private decimal? _value;
    [ObservableProperty] private CoinDenomination _denomination;

    public bool IsMoney => SelectedStat == StatLabels[0].Label;

    public EventConditionRowViewModel(EventCondition source, Action<EventConditionRowViewModel> remove)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(remove);
        _remove = remove;
        _selectedStat = StatLabels.First(s => s.Stat == source.Stat).Label;
        _selectedComparison = Game.Events.EventConditionEvaluator.Symbol(source.Comparison);
        _value = source.Value;
        _denomination = source.Denomination;
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    public EventCondition ToModel() => new()
    {
        Stat = StatLabels.First(s => s.Label == SelectedStat).Stat,
        Comparison = ComparisonOrder.First(c =>
            Game.Events.EventConditionEvaluator.Symbol(c) == SelectedComparison),
        Value = (long)Math.Max(0, Value ?? 0),
        Denomination = Denomination,
    };
}
