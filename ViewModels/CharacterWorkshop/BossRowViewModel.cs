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

// One boss row in the Bosses tab. Name + Rooms + StopBefore + ExactSpawn are
// user-editable and fire the section's onEdit (persist). RespawnDisplay /
// WindowsDisplay are computed from the game-data timer + active realm; StatusDisplay
// is the live countdown from BossTimerStore, refreshed on the heartbeat. Mark-killed
// / reset drive the timer manually (the auto-start is server-kill detection).
public sealed partial class BossRowViewModel : ObservableObject
{
    private readonly BossTimerStore _timers;
    private readonly Action _onEdit;
    private RealmType _realm;
    private bool _suppress;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _rooms = string.Empty;     // "map/room; map/room" text
    [ObservableProperty] private bool _stopBefore;
    [ObservableProperty] private bool _exactSpawn;

    // Computed (read-only) display, refreshed by the section.
    [ObservableProperty] private string _respawnDisplay = string.Empty;
    [ObservableProperty] private string _windowsDisplay = string.Empty;
    [ObservableProperty] private string _statusDisplay = string.Empty;
    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _isActive;

    public BossRespawnType RespawnType { get; }
    public bool IsTimed => RespawnType == BossRespawnType.Timed;
    public bool InStock { get; }
    public bool InParadigm { get; }
    public int? MonsterNumber { get; }

    public BossRowViewModel(BossDef def, RealmType realm, int? respawnHours, BossTimerStore timers, Action onEdit)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(onEdit);
        _timers = timers;
        _onEdit = onEdit;
        _suppress = true;
        Name = def.Name;
        Rooms = string.Join("; ", def.Rooms);
        StopBefore = def.StopBefore;
        ExactSpawn = def.ExactSpawn;
        RespawnType = def.RespawnType;
        InStock = def.InStock;
        InParadigm = def.InParadigm;
        MonsterNumber = def.MonsterNumber;
        RefreshDisplay(realm, respawnHours);
        _suppress = false;
    }

    // Recompute the read-only respawn + window columns for the active realm and the
    // game-data timer (null when the set has no such boss monster).
    public void RefreshDisplay(RealmType realm, int? respawnHours)
    {
        _realm = realm;
        IsCleanup = RespawnType == BossRespawnType.Cleanup;
        if (IsCleanup) { RespawnDisplay = "Cleanup"; WindowsDisplay = "—"; RefreshStatus(); return; }
        if (respawnHours is not { } full || full <= 0) { RespawnDisplay = "?"; WindowsDisplay = "(no game-data timer)"; RefreshStatus(); return; }

        RespawnDisplay = $"{full}h";
        // Early spawn points only (drop the trailing "full" — it's the Respawn column).
        var parts = BossTimerMath.SpawnFractions(realm, ExactSpawn)
            .Where(f => f < 1.0)
            .Select(f => $"{BossTimerMath.WindowLabel(realm, f)} {BossTimerMath.FormatHours(full * f)}")
            .ToList();
        WindowsDisplay = parts.Count > 0 ? string.Join("  ·  ", parts) : "exact (no early window)";
        RefreshStatus();
    }

    // Recompute the live countdown from the timer store. Blank when no timer is
    // running (never killed / expired / Cleanup). Called on the heartbeat.
    public void RefreshStatus()
    {
        BossWindowState? state = _timers.StatusFor(
            new BossDef { Name = Name, RespawnType = RespawnType, ExactSpawn = ExactSpawn }, _realm);
        if (state is not { } s) { IsActive = false; StatusDisplay = string.Empty; return; }
        IsActive = true;
        StatusDisplay = s.NextLabel == "full"
            ? $"full in {BossTimerMath.FormatDuration(s.FullRemaining)}"
            : $"{s.NextLabel} in {BossTimerMath.FormatDuration(s.NextRemaining)}  (full {BossTimerMath.FormatDuration(s.FullRemaining)})";
    }

    // Parse the Rooms text back to "map/room" tokens (accepts / or , separators, ; between).
    public List<string> ParseRooms()
    {
        var outp = new List<string>();
        foreach (string tok in Rooms.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = tok.Trim().Replace(',', '/');
            if (RoomKey.TryParseWire(t, out RoomKey k)) outp.Add($"{k.Map}/{k.Room}");
        }
        return outp;
    }

    public BossDef ToDef() => new()
    {
        Name = Name.Trim().ToLowerInvariant(),
        MonsterNumber = MonsterNumber,
        Rooms = ParseRooms(),
        InStock = InStock,
        InParadigm = InParadigm,
        RespawnType = RespawnType,
        ExactSpawn = ExactSpawn,
        StopBefore = StopBefore,
    };

    [RelayCommand]
    private void MarkKilled() { _timers.MarkKilled(Name.Trim().ToLowerInvariant()); RefreshStatus(); }

    [RelayCommand]
    private void ResetTimer() { _timers.Reset(Name.Trim().ToLowerInvariant()); RefreshStatus(); }

    partial void OnNameChanged(string value) => Edit();
    partial void OnRoomsChanged(string value) => Edit();
    partial void OnStopBeforeChanged(bool value) => Edit();
    partial void OnExactSpawnChanged(bool value) => Edit();

    private void Edit() { if (!_suppress) _onEdit(); }
}
