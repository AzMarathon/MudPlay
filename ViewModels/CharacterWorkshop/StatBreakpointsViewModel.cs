using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The Stat Breakpoints window, opened from a base-stat label on Character Info: every
// sub-stat the chosen stat feeds, the stat values where its share changes, and the
// row the character's own value falls in. Follows the live stats, the loaded realm
// and what the character swings with, so a trained point, a realm switch or a weapon
// swap redraws it.
public sealed partial class StatBreakpointsViewModel : ObservableObject, IDisposable
{
    public sealed record StatChoice(BaseStat Stat, string Name);

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private SwingBasis? _swing;

    public IReadOnlyList<StatChoice> StatChoices { get; } = new StatChoice[]
    {
        new(BaseStat.Strength, "Strength"), new(BaseStat.Intellect, "Intellect"),
        new(BaseStat.Willpower, "Willpower"), new(BaseStat.Agility, "Agility"),
        new(BaseStat.Health, "Health"), new(BaseStat.Charm, "Charm"),
    };

    public ObservableCollection<StatBreakpointColumnViewModel> Columns { get; } = new();

    [ObservableProperty] private StatChoice _selectedStat;
    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private bool _isParadigm;

    public BaseStat Stat => SelectedStat.Stat;

    // Raised after the columns are rebuilt, so the view can scroll each column to the
    // character's row.
    public event Action? Rebuilt;

    public StatBreakpointsViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory,
                                    BaseStat stat)
    {
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _selectedStat = StatChoices.First(c => c.Stat == stat);
        _stats.PropertyChanged += OnStatsChanged;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _inventory.Changed += OnInventoryChanged;
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

    // The inventory changes on every pickup; only a different weapon or load moves
    // Agility's swing-energy column, and a rebuild re-centres every column on the
    // character's row.
    private void OnInventoryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (Stat == BaseStat.Agility && ResolveSwing() != _swing) Rebuild();
    });

    // The wielded weapon, or a punch for a class that has one and holds no weapon, at
    // the load the swing calculation uses. Null when there is nothing to swing.
    private SwingBasis? ResolveSwing()
    {
        InventorySnapshot snap = _inventory.Snapshot;
        EquipmentStatSummary gear = CharacterCalculator.AggregateEquipmentStats(snap.EquippedItems, _gameData).Totals;
        EncumbranceReading load = snap.Encumbrance;
        int percent = load.MaxWeight > 0
            ? Math.Clamp((int)((long)load.CurrentWeight * 100 / load.MaxWeight), 0, 100)
            : 0;
        if (gear.WeaponSpeed > 0)
            return new SwingBasis($"your weapon (speed {gear.WeaponSpeed})", gear.WeaponSpeed, gear.WeaponStrReq, percent);
        if (ClassCapabilities.ClassHasPunch(_gameData.FindRowByName("Classes", _stats.Class)))
        {
            int speed = CombatCalculator.MartialArtsSpeed(MudAttackType.Punch, _gameData.ActiveRealm);
            return new SwingBasis($"a punch (speed {speed})", speed, 0, percent);
        }
        return null;
    }

    private void Rebuild()
    {
        RealmType realm = _gameData.ActiveRealm;
        StatContext ctx = StatContext.Resolve(_gameData, _stats.Class, _stats.Race, realm);
        var block = new StatBlock(_stats.Level, _stats.Strength, _stats.Intellect, _stats.Willpower,
            _stats.Agility, _stats.Health, _stats.Charm);
        int value = block.Get(Stat);
        int level = Math.Max(1, _stats.Level);

        IsParadigm = realm == RealmType.ParaMud;
        string realmName = IsParadigm ? "Paradigm" : "Stock";
        Heading = value > 0
            ? $"{SelectedStat.Name} {value} · {realmName} · level {level}"
            : $"{SelectedStat.Name} · {realmName} (no `stat` read yet)";
        Note = StatBreakpoints.Note(Stat, ctx);
        _swing = ResolveSwing();

        Columns.Clear();
        foreach (StatBreakpointColumn column in StatBreakpoints.For(Stat, ctx, level, maxStat: value + 10, block, _swing))
            Columns.Add(new StatBreakpointColumnViewModel(column, value));
        Rebuilt?.Invoke();
    }

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _inventory.Changed -= OnInventoryChanged;
    }
}
