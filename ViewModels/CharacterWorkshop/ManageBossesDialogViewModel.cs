using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Modeless editor for the boss list on the active realm — add / edit / remove
// entries (name, rooms, respawn type, flags). Loads the realm's bosses into
// editable rows; on Save it merges them back over the full catalog (the other
// realm's bosses are preserved) and writes the overlay delta. Cancel / X discards.
public sealed partial class ManageBossesDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly BossStore _bosses;
    private readonly RealmType _realm;

    public ObservableCollection<ManageBossRowViewModel> Rows { get; } = new();

    [ObservableProperty] private ManageBossRowViewModel? _selectedRow;

    public string HeaderText => $"Manage bosses — {(_realm == RealmType.ParaMud ? "ParaMUD" : "Stock")}";

    public ManageBossesDialogViewModel(BossStore bosses, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(gameData);
        _bosses = bosses;
        _realm = gameData.ActiveRealm;

        foreach (BossDef def in _bosses.ResolveForRealm(_realm)
                     .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new ManageBossRowViewModel(def));
    }

    [RelayCommand]
    private void AddRow()
    {
        var row = new ManageBossRowViewModel
        {
            InStock = _realm != RealmType.ParaMud,
            InParadigm = _realm == RealmType.ParaMud,
        };
        Rows.Add(row);
        SelectedRow = row;
    }

    [RelayCommand]
    private void RemoveRow(ManageBossRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null) Rows.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        var merged = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManageBossRowViewModel row in Rows)
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
