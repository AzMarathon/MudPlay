using System;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels;

// Read-only Roomba run log, opened from the GH Management tab's "Roomba Log"
// button. Surfaces the per-move record, the items left behind (with why), and an
// end-of-run summary (rooms sorted / items sorted / the explicit unmovable list).
// Refreshes live off GhSweepManager while a sweep runs.
public sealed partial class RoombaLogViewModel : ObservableObject, IDisposable
{
    private readonly GhSweepManager _sweep;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _movementLog = string.Empty;
    [ObservableProperty] private string _leftBehind = string.Empty;

    public RoombaLogViewModel(GhSweepManager sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        _sweep = sweep;
        _sweep.PhaseChanged += Refresh;
        _sweep.SweepCompleted += OnCompleted;
        Refresh();
    }

    private void OnCompleted(GhSweepReport report) => Refresh();

    // GhSweepManager is UI-thread-confined, so its events fire on the UI thread —
    // setting the observables directly here is safe.
    private void Refresh()
    {
        MovementLog = _sweep.MovedSoFar.Count == 0
            ? (_sweep.Mode == GhSweepManager.SweepMode.InventoryOnly
                ? "(inventory-only run — nothing is ever moved)"
                : "(nothing moved yet)")
            : string.Join("\n", _sweep.MovedSoFar.Select(m => $"{m.Count}x {m.ItemName}: {m.From} → {m.To}"));

        LeftBehind = _sweep.LeftInPlace.Count == 0
            ? "(nothing left behind)"
            : string.Join("\n", _sweep.LeftInPlace.Select(f => $"{f.ItemName} at {f.Room} ({DescribeReason(f.Reason)})"));

        int itemsSorted = _sweep.MovedSoFar.Sum(m => m.Count);
        int roomsSorted = _sweep.MovedSoFar.Select(m => m.From).Distinct().Count();
        var unmovable = _sweep.LeftInPlace.Where(f => f.Reason == GhLeftReason.TooHeavy).ToList();

        StringBuilder sb = new();
        if (_sweep.Mode == GhSweepManager.SweepMode.InventoryOnly)
            sb.AppendLine("Mode: inventory only — rooms observed and logged, nothing moved");
        sb.AppendLine($"Rooms sorted: {roomsSorted}");
        sb.AppendLine($"Items sorted: {itemsSorted}");
        if (unmovable.Count == 0)
        {
            sb.Append("Unmovable items: none");
        }
        else
        {
            sb.AppendLine($"Unmovable items ({unmovable.Count}) — too heavy to carry:");
            foreach (GhSweepItemFound f in unmovable) sb.AppendLine($"  • {f.ItemName} at {f.Room}");
        }
        if (_sweep.Stranded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Still carried — sweep ended before these were dropped ({_sweep.Stranded.Count}):");
            foreach (GhSweepStranded s in _sweep.Stranded)
                sb.Append($"  • {s.ItemName} — meant for {s.IntendedDestination}\n");
        }
        Summary = sb.ToString().TrimEnd();
    }

    private static string DescribeReason(GhLeftReason reason) => reason switch
    {
        GhLeftReason.TooHeavy => "too heavy to carry",
        GhLeftReason.GoneBySortTime => "gone by sort time",
        _ => "no matching room",
    };

    public void Dispose()
    {
        _sweep.PhaseChanged -= Refresh;
        _sweep.SweepCompleted -= OnCompleted;
    }
}
