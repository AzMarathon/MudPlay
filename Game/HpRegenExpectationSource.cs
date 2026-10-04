using System.ComponentModel;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.Game;

// Keeps the live character's HpRegenExpectation: level and Health from `stat`, the
// regen percent from worn gear, race, class and completed quests, in the loaded
// realm's formula. Worked out when asked and kept until a stat, the gear or the
// game-data set changes.
public sealed class HpRegenExpectationSource : IDisposable
{
    private readonly PlayerStats _stats;
    private readonly InventoryManager _inventory;
    private readonly GameDataCache _gameData;
    private readonly Func<IReadOnlyList<QuestBonus>?> _questBonuses;
    private HpRegenExpectation? _current;
    // Inventory changes can arrive off the UI thread; the value itself is only read
    // and rebuilt on it.
    private volatile bool _stale = true;

    public HpRegenExpectationSource(PlayerStats stats, InventoryManager inventory, GameDataCache gameData,
                                    Func<IReadOnlyList<QuestBonus>?> questBonuses)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _questBonuses = questBonuses ?? throw new ArgumentNullException(nameof(questBonuses));
        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += Invalidate;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    public HpRegenExpectation? Current
    {
        get
        {
            if (!_stale) return _current;
            _stale = false;
            EquipmentStatSummary totals = CharacterCalculator.CharacterTotals(
                _stats, _inventory.Snapshot.EquippedItems, _gameData, _questBonuses());
            _current = HpRegenExpectation.For(_gameData.ActiveRealm, _stats.Level, _stats.Health, totals.HpRegenPercent);
            return _current;
        }
    }

    // The quest log or the profile changed: work the amounts out again when next asked.
    public void Invalidate() => _stale = true;

    private void OnActiveSetChanged(string? setName) => Invalidate();

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerStats.Level) or nameof(PlayerStats.Health)
            or nameof(PlayerStats.Class) or nameof(PlayerStats.Race) or null or "")
            Invalidate();
    }

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= Invalidate;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }
}
