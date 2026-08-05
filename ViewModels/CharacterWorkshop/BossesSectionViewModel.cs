using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// BOSSES section — the per-realm boss list from BossStore, with respawn timers
// resolved from game data (BossCatalog.ResolveRegenHours). Realm-filtered (Stock
// hides Paradigm-only bosses) and realm-modeled (Stock shows the 87.5% early
// window; Paradigm shows −5/−10/−20%). The user can edit rooms + the StopBefore /
// ExactSpawn flags, add a custom boss, or remove one; every edit auto-saves to the
// set overlay (CP-tab convention). Live respawn timers + kill detection follow in a
// later phase; this tab is the editable catalog.
public sealed partial class BossesSectionViewModel : WorkshopSectionViewModel
{
    private readonly BossStore _bosses;
    private readonly GameDataCache _gameData;
    private Control? _view;
    private bool _suppress;

    public override string Id => "bosses";
    public override string Title => "Bosses";
    public override Control View => _view ??= new BossesSectionView { DataContext = this };

    public ObservableCollection<BossRowViewModel> Rows { get; } = new();

    [ObservableProperty] private BossRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isParadigmRealm;
    [ObservableProperty] private bool _hasBosses;

    // "Add boss" inputs.
    [ObservableProperty] private string _newBossName = string.Empty;
    [ObservableProperty] private string _newBossRooms = string.Empty;

    public BossesSectionViewModel(GameDataCache gameData, BossStore bosses)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(bosses);
        _gameData = gameData;
        _bosses = bosses;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        Rebuild();
    }

    public override void Dispose() => _gameData.ActiveSetChanged -= OnActiveSetChanged;

    private void OnActiveSetChanged(string? _) => Rebuild();

    private void Rebuild()
    {
        _suppress = true;
        RealmType realm = _gameData.ActiveRealm;
        IsParadigmRealm = realm == RealmType.ParaMud;
        Rows.Clear();
        foreach (BossDef def in _bosses.ResolveForRealm(realm)
                     .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            int? hrs = def.RespawnType == BossRespawnType.Timed
                ? BossCatalog.ResolveRegenHours(_gameData, def.Name)
                : null;
            Rows.Add(new BossRowViewModel(def, realm, hrs, OnRowEdited));
        }
        HasBosses = Rows.Count > 0;
        _suppress = false;
    }

    // Any row edit persists the whole list as an overlay delta, then refreshes the
    // computed columns (a name/rooms edit can change the resolved timer).
    private void OnRowEdited()
    {
        if (_suppress) return;
        Persist();
        RealmType realm = _gameData.ActiveRealm;
        foreach (BossRowViewModel row in Rows)
            row.RefreshDisplay(realm, row.RespawnType == BossRespawnType.Timed
                ? BossCatalog.ResolveRegenHours(_gameData, row.Name)
                : null);
    }

    // Persist the whole boss list. The visible rows FULLY govern the active realm
    // (so a removal on this realm actually drops the boss), while bosses belonging
    // only to the OTHER realm are carried through untouched — saving from a Stock
    // session must not delete Paradigm-only bosses, and vice-versa. A boss shared by
    // both realms is governed by the visible rows (edits + removals apply globally,
    // matching the shared, realm-wide store).
    private void Persist()
    {
        RealmType realm = _gameData.ActiveRealm;
        var merged = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BossRowViewModel row in Rows)
        {
            BossDef d = row.ToDef();
            if (seen.Add(d.Name)) merged.Add(d);
        }
        foreach (BossDef b in _bosses.Resolve())
        {
            bool visibleHere = realm == RealmType.ParaMud ? b.InParadigm : b.InStock;
            if (!visibleHere && seen.Add(b.Name)) merged.Add(b);
        }
        _bosses.Save(merged);
    }

    [RelayCommand]
    private void AddBoss()
    {
        string name = NewBossName.Trim().ToLowerInvariant();
        if (name.Length == 0) return;
        RealmType realm = _gameData.ActiveRealm;
        var def = new BossDef
        {
            Name = name,
            MonsterNumber = null,
            Rooms = new List<string>(),
            InStock = realm != RealmType.ParaMud,
            InParadigm = realm == RealmType.ParaMud,
            RespawnType = BossRespawnType.Timed,
        };
        var row = new BossRowViewModel(def, realm, BossCatalog.ResolveRegenHours(_gameData, name), OnRowEdited)
        { Rooms = NewBossRooms };
        Rows.Add(row);
        HasBosses = true;
        NewBossName = string.Empty;
        NewBossRooms = string.Empty;
        OnRowEdited();
    }

    [RelayCommand]
    private void RemoveRow(BossRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        Rows.Remove(row);
        HasBosses = Rows.Count > 0;
        OnRowEdited();
    }
}
