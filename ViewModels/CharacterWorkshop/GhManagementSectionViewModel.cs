using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using MudPlay.Views;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// ROOMBA section — Roomba Mode's control surface. Lists the character's labeled
// gang-house rooms (add via the map's "Toggle: Roomba Room" right-click or this
// tab's Add Room box; this tab reviews + removes and starts/stops the sweep) with
// a live per-room Status, a phase readout, and a double-click room-inventory view.
// The per-move record + end-of-run summary live in the separate Roomba Log window.
public sealed partial class GhManagementSectionViewModel : WorkshopSectionViewModel
{
    private readonly GhRoomLabelStore _labels;
    private readonly GhSweepManager _sweep;
    private readonly RoomGraphManager _roomGraph;
    private Control? _view;
    private RoombaLogWindow? _logWindow;
    private RoombaMasterListWindow? _masterListWindow;

    public override string Id => "ghmanagement";
    public override string Title => "Roomba";
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

    // "Add room" input — a map/room number the user types to label a room without
    // going to the map (e.g. "1/384").
    [ObservableProperty] private string _addRoomInput = string.Empty;

    // The per-room hidden-search count, editable here. _suppressSettingsWrite guards
    // the constructor's initial seed from the change hook that persists user edits.
    private bool _suppressSettingsWrite = true;
    [ObservableProperty] private int _searchesPerRoom;

    // Whether recon searches (`sea`) each room for hidden items. Off by default —
    // Roomba sorts the visible floor only unless the user opts in. BBS-wide
    // (shared by every character on this BBS).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchesEditable))]
    private bool _searchForHidden;

    // Whether @roomba <item> replies with the item's last-seen room. Off by
    // default — opt-in per BBS, shared by every character on it.
    [ObservableProperty] private bool _responsesEnabled;

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
        ResponsesEnabled = _labels.ResponsesEnabled;
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

    partial void OnResponsesEnabledChanged(bool value)
    {
        if (_suppressSettingsWrite) return;
        _labels.SetResponsesEnabled(value);
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
        bool inventoryOnly = _sweep.Mode == GhSweepManager.SweepMode.InventoryOnly;
        PhaseText = _sweep.Phase switch
        {
            GhSweepManager.SweepPhase.Reconning => inventoryOnly ? "Inventorying rooms…" : "Scanning rooms…",
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
        if (_sweep.Start(GhSweepManager.SweepMode.Sort)) { StartHint = null; CompletionSummary = null; }
        else StartHint = _sweep.LastStartError;
    }

    // Walks the same labeled circuit as Start (recon + hidden-item search per
    // the same settings) but never dispatches a get/drop — for a player who
    // wants @roomba's item-location log kept fresh without Roomba touching
    // (and potentially undoing) their own manual gang-house organization.
    [RelayCommand]
    private void StartInventory()
    {
        RoomContentsTitle = null;
        if (_sweep.Start(GhSweepManager.SweepMode.InventoryOnly)) { StartHint = null; CompletionSummary = null; }
        else StartHint = _sweep.LastStartError;
    }

    // SweepCompleted fires on the UI thread (GhSweepManager is UI-thread-confined),
    // so setting the observables directly is safe.
    private void OnSweepCompleted(GhSweepReport report)
    {
        CompletionSummary = _sweep.Mode == GhSweepManager.SweepMode.InventoryOnly
            ? $"Inventory scan complete — {_sweep.CircuitRoomCount} room(s) logged, nothing moved."
            : $"Sweep complete — {report.Moved.Count} move(s), {report.LeftInPlace.Count} left in place, "
              + $"{report.Stranded.Count} still carried.";
        RefreshRoomStatuses();
    }

    [RelayCommand]
    private void Stop() => _sweep.Stop("user stop from Roomba tab");

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

    // Master inventory across every room Roomba has ever scanned (item, room,
    // and outside market cross-reference), independent of any single sweep run.
    [RelayCommand]
    private void OpenMasterList()
    {
        if (_masterListWindow is { } open) { open.Activate(); return; }
        AppServices svc = AppServices.Current;
        RoombaMasterListViewModel vm = new(svc.GhItemLocations, _labels, svc.ItemNames, svc.GameData, _roomGraph);
        _masterListWindow = new RoombaMasterListWindow { DataContext = vm };
        _masterListWindow.Closed += (_, _) => _masterListWindow = null;
        _masterListWindow.Show();
    }

    // Add a room to the Roomba list by typed map/room number — opens the same rule
    // picker the map right-click uses, so the user still sets the room's sort rules.
    [RelayCommand]
    private async Task AddRoomAsync()
    {
        if (!TryParseRoomKey(AddRoomInput, out RoomKey key))
        {
            StartHint = "Enter a room as map/number, e.g. 1/384.";
            return;
        }
        string? name = _roomGraph.GetRoom(key)?.Name;
        _labels.TryGetLabel(key, out GhRoomLabel existing);
        GhRoomLabelPickerDialogViewModel picker = new(name ?? string.Empty, key.Map, key.Room, existing);
        GhRoomLabel? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<GhRoomLabelPickerDialogViewModel, GhRoomLabel?>(picker);
        if (result is null) return;   // cancelled
        _labels.SetLabel(key, result.Rules, result.IsCatchAll);
        AddRoomInput = string.Empty;
        StartHint = null;
    }

    // Parse "map/room" (also accepting space, comma, dash, or colon separators).
    private static bool TryParseRoomKey(string input, out RoomKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string[] parts = input.Split(new[] { '/', ' ', ',', '-', ':' },
            System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int map) || !int.TryParse(parts[1], out int room)) return false;
        key = new RoomKey(map, room);
        return true;
    }

    public override void Dispose()
    {
        _labels.Changed -= RebuildRooms;
        _sweep.PhaseChanged -= RefreshStatus;
        _sweep.SweepCompleted -= OnSweepCompleted;
        _logWindow?.Close();
        _masterListWindow?.Close();
    }
}
