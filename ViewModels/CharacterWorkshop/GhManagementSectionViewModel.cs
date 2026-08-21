using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Views;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// GH MANAGEMENT section — Roomba Mode's control surface. Lists the character's
// labeled gang-house rooms (right-click "Label as GH room…" on the map is the
// editor; this tab reviews + removes and starts/stops the sweep) with a live
// per-room Status, a phase readout, and a double-click room-inventory view. The
// per-move record + end-of-run summary live in the separate Roomba Log window.
public sealed partial class GhManagementSectionViewModel : WorkshopSectionViewModel
{
    private readonly GhRoomLabelStore _labels;
    private readonly GhSweepManager _sweep;
    private readonly RoomGraphManager _roomGraph;
    private Control? _view;
    private RoombaLogWindow? _logWindow;

    public override string Id => "ghmanagement";
    public override string Title => "GH Management";
    public override Control View => _view ??= new GhManagementSectionView { DataContext = this };

    public ObservableCollection<GhRoomLabelRowViewModel> Rooms { get; } = new();

    [ObservableProperty] private string _phaseText = "Idle";
    // A refused Start's reason (null = none), shown as a warning line on the tab so
    // clicking Start with too few labeled rooms (etc.) isn't a silent no-op.
    [ObservableProperty] private string? _startHint;
    // One-line "sweep finished" banner set from the SweepCompleted report; cleared
    // when the next sweep starts.
    [ObservableProperty] private string? _completionSummary;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchesEditable))]
    private bool _isRunning;

    // Double-click a room row → its current floor inventory (post-sweep, from the
    // final recon pass). Null title keeps the detail panel hidden.
    [ObservableProperty] private string? _roomContentsTitle;
    [ObservableProperty] private string _roomContentsText = string.Empty;

    // The per-room hidden-search count, editable here. _suppressSettingsWrite guards
    // the constructor's initial seed from the change hook that persists user edits.
    private bool _suppressSettingsWrite = true;
    [ObservableProperty] private int _searchesPerRoom;

    // Whether recon searches (`sea`) each room for hidden items. Off by default —
    // Roomba sorts the visible floor only unless the user opts in. Per-character.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchesEditable))]
    private bool _searchForHidden;

    // The searches-per-room count only matters while hidden-item search is on and
    // the sweep isn't running — grey the ticker out otherwise.
    public bool SearchesEditable => !IsRunning && SearchForHidden;

    public GhManagementSectionViewModel(GhRoomLabelStore labels, GhSweepManager sweep, RoomGraphManager roomGraph)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(roomGraph);
        _labels = labels;
        _sweep = sweep;
        _roomGraph = roomGraph;

        SearchesPerRoom = _labels.SearchesPerRoom;
        SearchForHidden = _labels.SearchForHidden;
        _suppressSettingsWrite = false;

        _labels.Changed += RebuildRooms;
        _sweep.PhaseChanged += RefreshStatus;
        _sweep.SweepCompleted += OnSweepCompleted;
        RebuildRooms();
        RefreshStatus();
    }

    partial void OnSearchesPerRoomChanged(int value)
    {
        if (_suppressSettingsWrite) return;
        _labels.SetSearchesPerRoom(value);
    }

    partial void OnSearchForHiddenChanged(bool value)
    {
        if (_suppressSettingsWrite) return;
        _labels.SetSearchForHidden(value);
    }

    private void RebuildRooms()
    {
        Rooms.Clear();
        foreach (GhRoomLabel label in _labels.Labels.OrderBy(l => l.Map).ThenBy(l => l.Room))
        {
            RoomKey key = new(label.Map, label.Room);
            string? name = _roomGraph.GetRoom(key)?.Name;
            Rooms.Add(new GhRoomLabelRowViewModel(label, name, OnRemoveRow));
        }
        RefreshRoomStatuses();
    }

    private void OnRemoveRow(GhRoomLabelRowViewModel row) => _labels.ClearLabel(row.Key);

    private void RefreshStatus()
    {
        IsRunning = _sweep.Phase != GhSweepManager.SweepPhase.Idle;
        PhaseText = _sweep.Phase switch
        {
            GhSweepManager.SweepPhase.Reconning => "Scanning rooms…",
            GhSweepManager.SweepPhase.Sorting => $"Sorting ({_sweep.MovedSoFar.Count} moved so far)",
            GhSweepManager.SweepPhase.FinalRecon => "Final scan…",
            _ => "Idle",
        };
        RefreshRoomStatuses();
    }

    private void RefreshRoomStatuses()
    {
        foreach (GhRoomLabelRowViewModel row in Rooms) row.Status = RoomStatus(row.Key);
    }

    private string RoomStatus(RoomKey key) => _sweep.Phase switch
    {
        GhSweepManager.SweepPhase.Reconning => "Scanning",
        GhSweepManager.SweepPhase.Sorting or GhSweepManager.SweepPhase.FinalRecon
            => _sweep.HasPendingPickupAt(key) ? "Cleaning" : "Complete",
        _ => CompletionSummary is null ? string.Empty : "Complete", // Idle: blank until a run finishes
    };

    [RelayCommand]
    private void Start()
    {
        RoomContentsTitle = null;
        if (_sweep.Start()) { StartHint = null; CompletionSummary = null; }
        else StartHint = _sweep.LastStartError;
    }

    // SweepCompleted fires on the UI thread (GhSweepManager is UI-thread-confined),
    // so setting the observables directly is safe.
    private void OnSweepCompleted(GhSweepReport report)
    {
        CompletionSummary =
            $"Sweep complete — {report.Moved.Count} move(s), {report.LeftInPlace.Count} left in place, "
            + $"{report.Stranded.Count} still carried.";
        RefreshRoomStatuses();
    }

    [RelayCommand]
    private void Stop() => _sweep.Stop("user stop from GH Management tab");

    // Invoked from the view's double-tap handler: show the room's current floor
    // inventory (from the final recon pass) in the detail panel.
    public void ShowRoomContents(GhRoomLabelRowViewModel row)
    {
        IReadOnlyList<string> items = _sweep.ObservedItemsAt(row.Key);
        RoomContentsTitle = $"{row.RoomName} ({row.RoomKeyText})";
        RoomContentsText = items.Count == 0
            ? "(nothing scanned here yet — run a sweep, or the floor is empty)"
            : string.Join("\n", items);
    }

    [RelayCommand]
    private void OpenRoombaLog()
    {
        if (_logWindow is { } open) { open.Activate(); return; }
        _logWindow = new RoombaLogWindow { DataContext = new RoombaLogViewModel(_sweep) };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    public override void Dispose()
    {
        _labels.Changed -= RebuildRooms;
        _sweep.PhaseChanged -= RefreshStatus;
        _sweep.SweepCompleted -= OnSweepCompleted;
        _logWindow?.Close();
    }
}
