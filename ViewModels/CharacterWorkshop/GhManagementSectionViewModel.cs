using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// GH MANAGEMENT section — Roomba Mode's control surface. Lists the character's
// labeled gang-house rooms (right-click "Label as GH room…" on the map is the
// editor; this tab reviews + removes and starts/stops the sweep), a live phase
// readout, and the moved / left-in-place report as the sweep progresses.
public sealed partial class GhManagementSectionViewModel : WorkshopSectionViewModel
{
    private readonly GhRoomLabelStore _labels;
    private readonly GhSweepManager _sweep;
    private readonly RoomGraphManager _roomGraph;
    private Control? _view;

    public override string Id => "ghmanagement";
    public override string Title => "GH Management";
    public override Control View => _view ??= new GhManagementSectionView { DataContext = this };

    public ObservableCollection<GhRoomLabelRowViewModel> Rooms { get; } = new();

    [ObservableProperty] private string _phaseText = "Idle";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchesEditable))]
    private bool _isRunning;
    [ObservableProperty] private string _movedText = string.Empty;
    [ObservableProperty] private string _leftInPlaceText = string.Empty;
    [ObservableProperty] private string _strandedText = string.Empty;
    [ObservableProperty] private bool _hasStranded;

    // Recon tuning, editable here — see GhRoomLabelStore for persistence /
    // defaults (2 laps, 3 searches per room). _suppressSettingsWrite guards
    // the constructor's initial assignment from the [ObservableProperty]
    // change hooks below, which persist on every user edit but shouldn't
    // fire while just seeding the current value from the store.
    private bool _suppressSettingsWrite = true;
    [ObservableProperty] private int _reconLaps;
    [ObservableProperty] private int _searchesPerRoom;

    // Whether recon searches (`sea`) each room for hidden items. Off by default —
    // Roomba sorts the visible floor only unless the user opts in. Persisted
    // per-character via the store.
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

        ReconLaps = _labels.ReconLaps;
        SearchesPerRoom = _labels.SearchesPerRoom;
        SearchForHidden = _labels.SearchForHidden;
        _suppressSettingsWrite = false;

        _labels.Changed += RebuildRooms;
        _sweep.PhaseChanged += RefreshStatus;
        RebuildRooms();
        RefreshStatus();
    }

    partial void OnReconLapsChanged(int value)
    {
        if (_suppressSettingsWrite) return;
        _labels.SetReconLaps(value);
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
    }

    private void OnRemoveRow(GhRoomLabelRowViewModel row) => _labels.ClearLabel(row.Key);

    private void RefreshStatus()
    {
        IsRunning = _sweep.Phase != GhSweepManager.SweepPhase.Idle;
        PhaseText = _sweep.Phase switch
        {
            GhSweepManager.SweepPhase.Reconning => $"Reconning (lap {_sweep.CompletedReconLaps}/{_labels.ReconLaps})",
            GhSweepManager.SweepPhase.Sorting => $"Sorting ({_sweep.MovedSoFar.Count} moved so far)",
            _ => "Idle",
        };
        MovedText = _sweep.MovedSoFar.Count == 0
            ? "(none yet)"
            : string.Join("\n", _sweep.MovedSoFar.Select(m => $"{m.ItemName}: {m.From} -> {m.To}"));
        LeftInPlaceText = _sweep.LeftInPlace.Count == 0
            ? "(none)"
            : string.Join("\n", _sweep.LeftInPlace.Select(f => $"{f.ItemName} at {f.Room}"));
        HasStranded = _sweep.Stranded.Count > 0;
        StrandedText = _sweep.Stranded.Count == 0
            ? "(none)"
            : string.Join("\n", _sweep.Stranded.Select(s => $"{s.ItemName}: carrying from {s.CarriedFrom}, meant for {s.IntendedDestination}"));
    }

    [RelayCommand]
    private void Start() => _sweep.Start();

    [RelayCommand]
    private void Stop() => _sweep.Stop("user stop from GH Management tab");

    public override void Dispose()
    {
        _labels.Changed -= RebuildRooms;
        _sweep.PhaseChanged -= RefreshStatus;
    }
}
