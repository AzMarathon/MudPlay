using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// BOSSES section — the per-realm boss list from BossStore, with respawn timers
// resolved from game data. The table is read-mostly: StopBefore toggles inline and
// per-row Mark / Reset drive the timer; adding / editing / removing bosses happens
// in the Manage Bosses dialog. Live countdowns refresh on the heartbeat.
public sealed partial class BossesSectionViewModel : WorkshopSectionViewModel
{
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly TickEngine _tick;
    private Control? _view;
    private bool _suppress;

    public override string Id => "bosses";
    public override string Title => "Bosses";
    public override Control View => _view ??= new BossesSectionView { DataContext = this };

    public ObservableCollection<BossRowViewModel> Rows { get; } = new();

    [ObservableProperty] private BossRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isParadigmRealm;
    [ObservableProperty] private bool _hasBosses;
    [ObservableProperty] private string _activeSummary = string.Empty;

    public BossesSectionViewModel(GameDataCache gameData, BossStore bosses, BossTimerStore timers, TickEngine tick)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(tick);
        _gameData = gameData;
        _bosses = bosses;
        _timers = timers;
        _tick = tick;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _timers.Changed += OnTimersChanged;
        _tick.HeartbeatElapsed += OnHeartbeat;
        Rebuild();
    }

    public override void Dispose()
    {
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _timers.Changed -= OnTimersChanged;
        _tick.HeartbeatElapsed -= OnHeartbeat;
    }

    private void OnActiveSetChanged(string? _) => Rebuild();

    private void OnTimersChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshStatuses();
        else Dispatcher.UIThread.Post(RefreshStatuses);
    }

    private void OnHeartbeat() => RefreshStatuses();

    private void RefreshStatuses()
    {
        foreach (BossRowViewModel row in Rows) row.RefreshStatus();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int active = _timers.ActiveTimers(_gameData.ActiveRealm).Count;
        ActiveSummary = active == 0 ? "No boss timers active" : $"{active} boss timer{(active == 1 ? "" : "s")} active";
    }

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
            Rows.Add(new BossRowViewModel(def, realm, hrs, _timers, OnRowEdited, OnMarkRequested));
        }
        HasBosses = Rows.Count > 0;
        _suppress = false;
        UpdateSummary();
    }

    // StopBefore toggled inline → persist the whole list as an overlay delta.
    private void OnRowEdited()
    {
        if (_suppress) return;
        Persist();
    }

    // Persist the visible rows over the full resolved list. The visible rows fully
    // govern the active realm; the other realm's bosses are carried through
    // untouched. (Add / edit / remove of names + rooms goes through the Manage
    // dialog, which re-saves and triggers a rebuild; this path only carries a
    // StopBefore toggle.)
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

    // Row's Mark button — open the modeless mark-time dialog (defaults to now,
    // user-editable) and stamp the chosen time on commit.
    private async void OnMarkRequested(BossRowViewModel row)
    {
        DateTimeOffset? at = await AppServices.Current.Dialogs
            .OpenWindowAsync<MarkTimerDialogViewModel, DateTimeOffset?>(
                new MarkTimerDialogViewModel(row.Name, DateTimeOffset.Now));
        if (at is { } when)
        {
            _timers.MarkKilled(row.Name.Trim().ToLowerInvariant(), when);
            RefreshStatuses();
        }
    }

    [RelayCommand]
    private async Task ManageBosses()
    {
        bool saved = await AppServices.Current.Dialogs
            .OpenWindowAsync<ManageBossesDialogViewModel, bool>(
                new ManageBossesDialogViewModel(_bosses, _gameData));
        if (saved) Rebuild();
    }

    [RelayCommand]
    private void ResetSelected()
    {
        if (SelectedRow is { } row) { _timers.Reset(row.Name.Trim().ToLowerInvariant()); RefreshStatuses(); }
    }
}
