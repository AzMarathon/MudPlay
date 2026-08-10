using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// DEATH section — the Death Recovery surface. Binds to DeathRecoveryManager for
// the deathpile record grid, the Auto-Recover / Auto-Equip toggles, and the
// recovery actions (Walk to Room / Recover Now / Mark Recovered / Clear). Current
// lives are shown on the Character Info tab, not here.
public sealed partial class DeathSectionViewModel : WorkshopSectionViewModel
{
    private readonly DeathRecoveryManager _recovery;
    private readonly ProfileService _profile;
    private Control? _view;

    public override string Id => "death";
    public override string Title => "Death Recovery";
    public override Control View => _view ??= new DeathSectionView { DataContext = this };

    [ObservableProperty] private IReadOnlyList<DeathRecord> _records = Array.Empty<DeathRecord>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRecoveredCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(HowDidIDieCommand))]
    private DeathRecord? _selectedRecord;

    public DeathSectionViewModel(DeathRecoveryManager recovery, ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);
        _recovery = recovery;
        _profile = profile;
        _recovery.PropertyChanged += OnRecoveryChanged;
        _profile.ProfileLoaded += OnProfileChanged;
        Refresh();
    }

    // Auto-grab a deathpile's lost items on re-entry. Two-way bound to the
    // manager's persisted per-character toggle. Inert on the actual grab until
    // inventory tracking lands.
    public bool AutoRecover
    {
        get => _recovery.AutoRecover;
        set { if (_recovery.AutoRecover != value) { _recovery.AutoRecover = value; OnPropertyChanged(); } }
    }

    // Re-equip items worn at death after recovery. Two-way bound to the manager.
    public bool AutoEquip
    {
        get => _recovery.AutoEquip;
        set { if (_recovery.AutoEquip != value) { _recovery.AutoEquip = value; OnPropertyChanged(); } }
    }

    // Recovery-status blurb for the detail panel — the last recovery note, plus
    // (while still Partial) the pile items not yet seen picked up, so "why is this
    // Partial?" is answered in-place instead of hidden in a grid-row tooltip
    // (report stock-20260730-214157). Empty for an Active record with no note.
    public string RecoveryDetailText
    {
        get
        {
            if (SelectedRecord is not { } r) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.RecoveryMessage)) parts.Add(r.RecoveryMessage!);
            if (r.Status == DeathRecoveryStatus.Partial && r.UnrecoveredItems is { Count: > 0 })
                parts.Add("Still to recover: " + string.Join(", ", r.UnrecoveredItems));
            return string.Join("\n", parts);
        }
    }

    // Drives the detail panel's recovery section visibility.
    public bool HasRecoveryDetail => RecoveryDetailText.Length > 0;

    partial void OnSelectedRecordChanged(DeathRecord? value)
    {
        OnPropertyChanged(nameof(RecoveryDetailText));
        OnPropertyChanged(nameof(HasRecoveryDetail));
    }

    // Re-pull observables + record list from the manager.
    public void Refresh()
    {
        DeathRecord? prevSelected = SelectedRecord;
        Records = _recovery.Records.OrderByDescending(r => r.RecordNumber).ToList();
        // Keep the selection pinned to the same record across refresh
        // when it still exists; otherwise drop it.
        SelectedRecord = prevSelected is not null && Records.Contains(prevSelected)
            ? prevSelected
            : null;

        OnPropertyChanged(nameof(AutoRecover));
        OnPropertyChanged(nameof(AutoEquip));
        // The pinned record's Status / UnrecoveredItems may have changed without a
        // reference change, so force the recovery blurb to re-read.
        OnPropertyChanged(nameof(RecoveryDetailText));
        OnPropertyChanged(nameof(HasRecoveryDetail));
        ClearAllRecoveredCommand.NotifyCanExecuteChanged();
    }

    // Open the death-log replay for the selected record — the backscroll tail
    // frozen at that death. CanExecute keys on the cheap DeathLogFile pointer (no
    // disk I/O); the read itself may still come back empty if the file was
    // removed out from under us, in which case we say so rather than pop a blank.
    [RelayCommand(CanExecute = nameof(CanShowDeathLog))]
    private async Task HowDidIDie()
    {
        if (SelectedRecord is not { } r) return;
        string? log = _recovery.ReadDeathLog(r);
        if (string.IsNullOrEmpty(log))
        {
            AppServices.Current.Dialogs.ShowInfo(
                "How did I Die?",
                "No death log was captured for this death (or its file is gone). " +
                "Death logs are only recorded for deaths that happen while connected, " +
                "on a saved character.");
            return;
        }
        string title = $"How did I Die? — {r.RoomName ?? "Unknown room"} · {r.DiedText}";
        await AppServices.Current.Dialogs
            .OpenWindowAsync<DeathLogViewModel, bool>(new DeathLogViewModel(title, log));
    }

    // Double-clicking a death row opens/focuses the Navigation window and centres
    // the map on the death's map/room. No-op when the death room was unknown at
    // capture (Room is null — the grid shows "—" and there's nowhere to centre).
    public void ShowSelectedOnMap()
    {
        if (SelectedRecord?.Room is not { } room) return;
        AppServices.Current.NavigateToRoom(new Game.Map.RoomKey(room.Map, room.Room));
    }

    [RelayCommand(CanExecute = nameof(CanRecoverSelected))]
    private void RecoverNow() { if (SelectedRecord is { } r) _recovery.RecoverNow(r); }

    [RelayCommand(CanExecute = nameof(CanMarkRecovered))]
    private void MarkRecovered() { if (SelectedRecord is { } r) _recovery.MarkRecovered(r); }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelected() { if (SelectedRecord is { } r) _recovery.ClearSelected(r); }

    [RelayCommand(CanExecute = nameof(CanClearAllRecovered))]
    private void ClearAllRecovered() => _recovery.ClearAllRecovered();

    [RelayCommand]
    private void SimulateDeath() => _recovery.SimulateDeath();

    private bool HasSelection() => SelectedRecord is not null;
    private bool CanShowDeathLog() => SelectedRecord?.DeathLogFile is { Length: > 0 };
    private bool CanRecoverSelected() => SelectedRecord is { Room: not null } r && r.Status != DeathRecoveryStatus.Recovered;
    private bool CanMarkRecovered() => SelectedRecord is { } r && r.Status != DeathRecoveryStatus.Recovered;
    private bool CanClearAllRecovered() => Records.Any(r => r.Status == DeathRecoveryStatus.Recovered);

    private void OnRecoveryChanged(object? sender, PropertyChangedEventArgs _) => Refresh();
    private void OnProfileChanged(CharacterProfile _) => Refresh();

    public override void Dispose()
    {
        _recovery.PropertyChanged -= OnRecoveryChanged;
        _profile.ProfileLoaded -= OnProfileChanged;
    }
}
