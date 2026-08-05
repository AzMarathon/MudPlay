using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Modeless editor for the boss list on the active realm — add / edit / remove
// entries (name, rooms, respawn type, flags). Loads the realm's bosses into
// editable rows; on Save it merges them back over the full catalog (the other
// realm's bosses are preserved) and writes the overlay delta. Cancel / X discards.
// A filter box narrows the grid by boss name or room.
public sealed partial class ManageBossesDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly BossStore _bosses;
    private readonly GameDataCache _gameData;
    private readonly RealmType _realm;
    private readonly ObservableCollection<ManageBossRowViewModel> _allRows = new();

    // The grid binds this filtered view; Add / Remove / Save operate on the backing
    // collection so a live filter never hides an edit from the save.
    public DataGridCollectionView Rows { get; }

    [ObservableProperty] private ManageBossRowViewModel? _selectedRow;
    [ObservableProperty] private string _filterText = string.Empty;

    public string HeaderText => $"Manage bosses — {(_realm == RealmType.ParaMud ? "ParaMUD" : "Stock")}";

    public ManageBossesDialogViewModel(BossStore bosses, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(gameData);
        _bosses = bosses;
        _gameData = gameData;
        _realm = gameData.ActiveRealm;

        foreach (BossDef def in _bosses.ResolveForRealm(_realm)
                     .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            _allRows.Add(new ManageBossRowViewModel(def, BossCatalog.ResolveRegenHours(_gameData, def.Name)));

        Rows = new DataGridCollectionView(_allRows);
    }

    // Filter by boss name OR room substring (case-insensitive); empty clears it.
    partial void OnFilterTextChanged(string value)
    {
        string q = value.Trim();
        Rows.Filter = q.Length == 0
            ? null
            : o => o is ManageBossRowViewModel r
                   && (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || r.Rooms.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void AddRow()
    {
        FilterText = string.Empty;   // clear any filter so the new blank row is visible
        var row = new ManageBossRowViewModel
        {
            InStock = _realm != RealmType.ParaMud,
            InParadigm = _realm == RealmType.ParaMud,
        };
        _allRows.Add(row);
        SelectedRow = row;
    }

    [RelayCommand]
    private void RemoveRow(ManageBossRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null) _allRows.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        var merged = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManageBossRowViewModel row in _allRows)
        {
            BossDef d = row.ToDef();
            if (d.Name.Length == 0) continue;           // drop blank rows
            if (seen.Add(d.Name)) merged.Add(d);
        }
        // Preserve bosses that belong only to the other realm (not shown here).
        foreach (BossDef b in _bosses.Resolve())
        {
            bool visibleHere = _realm == RealmType.ParaMud ? b.InParadigm : b.InStock;
            if (!visibleHere && seen.Add(b.Name)) merged.Add(b);
        }
        _bosses.Save(merged);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
