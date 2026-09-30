using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The Stat Breakpoints window, opened from a base-stat label on Character Info: every
// sub-stat the chosen stat feeds, the stat values where its share changes, and the
// row the character's own value falls in. Follows the live stats and the loaded
// realm, so a trained point or a realm switch redraws it.
public sealed partial class StatBreakpointsViewModel : ObservableObject, IDisposable
{
    public sealed record StatChoice(BaseStat Stat, string Name);

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;

    public IReadOnlyList<StatChoice> StatChoices { get; } = new StatChoice[]
    {
        new(BaseStat.Strength, "Strength"), new(BaseStat.Intellect, "Intellect"),
        new(BaseStat.Willpower, "Willpower"), new(BaseStat.Agility, "Agility"),
        new(BaseStat.Health, "Health"), new(BaseStat.Charm, "Charm"),
    };

    public ObservableCollection<StatBreakpointColumnViewModel> Columns { get; } = new();

    [ObservableProperty] private StatChoice _selectedStat;
    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _everyPointNote = string.Empty;
    [ObservableProperty] private bool _isParadigm;

    public BaseStat Stat => SelectedStat.Stat;

    // Raised after the columns are rebuilt, so the view can scroll each column to the
    // character's row.
    public event Action? Rebuilt;

    public StatBreakpointsViewModel(PlayerStats stats, GameDataCache gameData, BaseStat stat)
    {
        _stats = stats;
        _gameData = gameData;
        _selectedStat = StatChoices.First(c => c.Stat == stat);
        _stats.PropertyChanged += OnStatsChanged;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        Rebuild();
    }

    public void Show(BaseStat stat) => SelectedStat = StatChoices.First(c => c.Stat == stat);

    partial void OnSelectedStatChanged(StatChoice value) => Rebuild();

    // HP and mana change on every prompt; only the six stats, level and class/race
    // move these tables.
    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerStats.Strength): case nameof(PlayerStats.Intellect):
            case nameof(PlayerStats.Willpower): case nameof(PlayerStats.Agility):
            case nameof(PlayerStats.Health): case nameof(PlayerStats.Charm):
            case nameof(PlayerStats.Level): case nameof(PlayerStats.Class): case nameof(PlayerStats.Race):
            case null: case "":
                Rebuild();
                break;
        }
    }

    private void OnActiveSetChanged(string? setName) => Rebuild();

    private void Rebuild()
    {
        RealmType realm = _gameData.ActiveRealm;
        StatContext ctx = StatContext.Resolve(_gameData, _stats.Class, _stats.Race, realm);
        int value = new StatBlock(_stats.Level, _stats.Strength, _stats.Intellect, _stats.Willpower,
            _stats.Agility, _stats.Health, _stats.Charm).Get(Stat);
        int level = Math.Max(1, _stats.Level);

        IsParadigm = realm == RealmType.ParaMud;
        string realmName = IsParadigm ? "Paradigm" : "Stock";
        Heading = value > 0
            ? $"{SelectedStat.Name} {value} · {realmName} · level {level}"
            : $"{SelectedStat.Name} · {realmName} (no `stat` read yet)";
        EveryPointNote = StatBreakpoints.EveryPointNote(Stat, realm);

        Columns.Clear();
        foreach (StatBreakpointColumn column in StatBreakpoints.For(Stat, ctx, level, maxStat: value + 10))
            Columns.Add(new StatBreakpointColumnViewModel(column, value));
        Rebuilt?.Invoke();
    }

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }
}
