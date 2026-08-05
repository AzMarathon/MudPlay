using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One boss row in the Bosses tab. Name + Rooms + StopBefore + ExactSpawn are
// user-editable and fire the section's onEdit (persist). RespawnDisplay /
// WindowsDisplay are computed from the game-data timer + active realm and set by
// the section on (re)build — the tab shows the realm's spawn-window model.
public sealed partial class BossRowViewModel : ObservableObject
{
    private readonly Action _onEdit;
    private bool _suppress;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _rooms = string.Empty;     // "map/room; map/room" text
    [ObservableProperty] private bool _stopBefore;
    [ObservableProperty] private bool _exactSpawn;

    // Computed (read-only) display, refreshed by the section.
    [ObservableProperty] private string _respawnDisplay = string.Empty;
    [ObservableProperty] private string _windowsDisplay = string.Empty;
    [ObservableProperty] private bool _isCleanup;

    public BossRespawnType RespawnType { get; }
    public bool InStock { get; }
    public bool InParadigm { get; }
    public int? MonsterNumber { get; }

    public BossRowViewModel(BossDef def, RealmType realm, int? respawnHours, Action onEdit)
    {
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
        IsCleanup = RespawnType == BossRespawnType.Cleanup;
        if (IsCleanup) { RespawnDisplay = "Cleanup"; WindowsDisplay = "—"; return; }
        if (respawnHours is not { } full || full <= 0) { RespawnDisplay = "?"; WindowsDisplay = "(no game-data timer)"; return; }

        RespawnDisplay = $"{full}h";
        // Early spawn points only (drop the trailing "full" — it's the Respawn column).
        var parts = BossTimerMath.SpawnFractions(realm, ExactSpawn)
            .Where(f => f < 1.0)
            .Select(f => $"{BossTimerMath.FractionLabel(f)} {BossTimerMath.FormatHours(full * f)}")
            .ToList();
        WindowsDisplay = parts.Count > 0 ? string.Join("  ·  ", parts) : "exact (no early window)";
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

    partial void OnNameChanged(string value) => Edit();
    partial void OnRoomsChanged(string value) => Edit();
    partial void OnStopBeforeChanged(bool value) => Edit();
    partial void OnExactSpawnChanged(bool value) => Edit();

    private void Edit() { if (!_suppress) _onEdit(); }
}
