using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One boss row in the Bosses tab. StopBefore is the only inline-editable field
// (a quick toggle); name / rooms / flags are edited in the Manage Bosses dialog.
// The live timer columns — the 100% countdown (StatusDisplay) and the per-window
// early countdowns (Early1/2/3) — are recomputed on the heartbeat: each early
// column counts down to its spawn window and blanks once that window has passed.
// Every timer column also exposes a numeric sort key so the DataGrid can sort by
// remaining time (blank / expired rows sort last).
public sealed partial class BossRowViewModel : ObservableObject
{
    private const long InactiveSort = long.MaxValue;

    private readonly BossTimerStore _timers;
    private readonly Action _onEdit;
    private readonly Action<BossRowViewModel> _onMarkRequested;
    private RealmType _realm;
    private int? _respawnHours;
    private bool _suppress;

    // Displayed name (read-only in the grid). Rooms / flags are held for persist but
    // edited only in the Manage Bosses dialog.
    [ObservableProperty] private string _name = string.Empty;
    public string Rooms { get; set; } = string.Empty;   // "map/room; map/room"
    public bool ExactSpawn { get; set; }

    [ObservableProperty] private bool _stopBefore;

    // Static respawn length ("10h" / "Cleanup" / "?") + its sort key (hours).
    [ObservableProperty] private string _respawnDisplay = string.Empty;
    [ObservableProperty] private int _respawnSortKey = int.MaxValue;

    // Live 100% countdown + sort key (seconds remaining; InactiveSort when idle).
    [ObservableProperty] private string _statusDisplay = string.Empty;
    [ObservableProperty] private long _fullSortKey = InactiveSort;

    // Live early-window countdowns (display order 5% / 10% / 20% on Paradigm; the
    // single 87.5% on Stock in slot 1) + sort keys.
    [ObservableProperty] private string _early1Display = string.Empty;
    [ObservableProperty] private string _early2Display = string.Empty;
    [ObservableProperty] private string _early3Display = string.Empty;
    [ObservableProperty] private long _early1SortKey = InactiveSort;
    [ObservableProperty] private long _early2SortKey = InactiveSort;
    [ObservableProperty] private long _early3SortKey = InactiveSort;

    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _isActive;

    public BossRespawnType RespawnType { get; }
    public bool IsTimed => RespawnType == BossRespawnType.Timed;
    public bool InStock { get; }
    public bool InParadigm { get; }
    public int? MonsterNumber { get; }

    public BossRowViewModel(
        BossDef def, RealmType realm, int? respawnHours, BossTimerStore timers,
        Action onEdit, Action<BossRowViewModel> onMarkRequested)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(onEdit);
        ArgumentNullException.ThrowIfNull(onMarkRequested);
        _timers = timers;
        _onEdit = onEdit;
        _onMarkRequested = onMarkRequested;
        _suppress = true;
        Name = def.Name;
        Rooms = BossRoomText.Format(def.Rooms);
        StopBefore = def.StopBefore;
        ExactSpawn = def.ExactSpawn;
        RespawnType = def.RespawnType;
        InStock = def.InStock;
        InParadigm = def.InParadigm;
        MonsterNumber = def.MonsterNumber;
        RefreshDisplay(realm, respawnHours);
        _suppress = false;
    }

    // Recompute the static respawn column for the realm + game-data timer, then the
    // live columns.
    public void RefreshDisplay(RealmType realm, int? respawnHours)
    {
        _realm = realm;
        _respawnHours = respawnHours;
        IsCleanup = RespawnType == BossRespawnType.Cleanup;
        if (IsCleanup) { RespawnDisplay = "Cleanup"; RespawnSortKey = int.MaxValue; }
        else if (respawnHours is { } full && full > 0) { RespawnDisplay = $"{full}h"; RespawnSortKey = full; }
        else { RespawnDisplay = "?"; RespawnSortKey = int.MaxValue; }
        RefreshStatus();
    }

    // Recompute the live 100% + early-window countdowns from the kill time. All
    // blank when no timer is running (never killed / expired / Cleanup / no timer).
    public void RefreshStatus()
    {
        DateTimeOffset? killed = _timers.KilledAt(Name);
        if (IsCleanup || killed is not { } k || _respawnHours is not { } full || full <= 0)
        {
            ClearLive();
            return;
        }
        double fullSecs = full * 3600.0;
        double elapsed = (DateTimeOffset.UtcNow - k).TotalSeconds;
        if (elapsed >= fullSecs) { ClearLive(); return; }   // expired → not tracking

        IsActive = true;
        double fullRem = fullSecs - elapsed;
        StatusDisplay = BossTimerMath.FormatDuration(TimeSpan.FromSeconds(fullRem));
        FullSortKey = (long)fullRem;

        IReadOnlyList<double> fracs = BossTimerMath.EarlyFractionsInDisplayOrder(_realm, ExactSpawn);
        (Early1Display, Early1SortKey) = Window(fracs, 0, elapsed, fullSecs);
        (Early2Display, Early2SortKey) = Window(fracs, 1, elapsed, fullSecs);
        (Early3Display, Early3SortKey) = Window(fracs, 2, elapsed, fullSecs);
    }

    // Countdown to the i-th early window, or blank once it has passed (or there's no
    // such window for this realm). Blank sorts last via InactiveSort.
    private static (string display, long sort) Window(IReadOnlyList<double> fracs, int i, double elapsed, double fullSecs)
    {
        if (i >= fracs.Count) return (string.Empty, InactiveSort);
        double rem = fracs[i] * fullSecs - elapsed;
        return rem > 0
            ? (BossTimerMath.FormatDuration(TimeSpan.FromSeconds(rem)), (long)rem)
            : (string.Empty, InactiveSort);
    }

    private void ClearLive()
    {
        IsActive = false;
        StatusDisplay = string.Empty; FullSortKey = InactiveSort;
        Early1Display = Early2Display = Early3Display = string.Empty;
        Early1SortKey = Early2SortKey = Early3SortKey = InactiveSort;
    }

    public BossDef ToDef() => new()
    {
        Name = Name.Trim().ToLowerInvariant(),
        MonsterNumber = MonsterNumber,
        Rooms = BossRoomText.Parse(Rooms),
        InStock = InStock,
        InParadigm = InParadigm,
        RespawnType = RespawnType,
        ExactSpawn = ExactSpawn,
        StopBefore = StopBefore,
    };

    [RelayCommand]
    private void Mark() => _onMarkRequested(this);

    [RelayCommand]
    private void ResetTimer() { _timers.Reset(Name.Trim().ToLowerInvariant()); RefreshStatus(); }

    partial void OnStopBeforeChanged(bool value) { if (!_suppress) _onEdit(); }
}
