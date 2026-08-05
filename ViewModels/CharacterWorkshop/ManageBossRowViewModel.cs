using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One editable boss entry in the Manage Bosses dialog. All fields are editable; a
// new row starts blank (visible on whichever realm the dialog was opened for).
// RespawnHoursText is the respawn length in hours: pre-filled from game data (or a
// prior override) and editable — a value that differs from game data is saved as a
// per-boss override, so a boss game data can't resolve (shown "?" on the tab) can be
// corrected here.
public sealed partial class ManageBossRowViewModel : ObservableObject
{
    // Game-data respawn hours for this boss (null when the set can't resolve one);
    // used to decide whether the typed value is an override worth storing.
    private readonly int? _gameDataHours;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _rooms = string.Empty;   // "map/room; map/room"
    [ObservableProperty] private string _respawnHoursText = string.Empty;
    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _exactSpawn;
    [ObservableProperty] private bool _inStock;
    [ObservableProperty] private bool _inParadigm;

    public int? MonsterNumber { get; private set; }
    private bool _stopBefore;

    public ManageBossRowViewModel() { }

    public ManageBossRowViewModel(BossDef def, int? gameDataHours)
    {
        _gameDataHours = gameDataHours;
        Name = def.Name;
        Rooms = BossRoomText.Format(def.Rooms);
        int? shown = def.RespawnHoursOverride ?? gameDataHours;
        RespawnHoursText = shown?.ToString() ?? string.Empty;
        IsCleanup = def.RespawnType == BossRespawnType.Cleanup;
        ExactSpawn = def.ExactSpawn;
        _stopBefore = def.StopBefore;
        InStock = def.InStock;
        InParadigm = def.InParadigm;
        MonsterNumber = def.MonsterNumber;
    }

    public BossDef ToDef() => new()
    {
        Name = Name.Trim().ToLowerInvariant(),
        MonsterNumber = MonsterNumber,
        Rooms = BossRoomText.Parse(Rooms),
        InStock = InStock,
        InParadigm = InParadigm,
        RespawnType = IsCleanup ? BossRespawnType.Cleanup : BossRespawnType.Timed,
        ExactSpawn = ExactSpawn,
        StopBefore = _stopBefore,   // edited on the main table, carried through unchanged
        RespawnHoursOverride = ResolveOverride(),
    };

    // A typed hours value only becomes a stored override when it's a positive number
    // that differs from what game data already gives (so the overlay stays a delta);
    // blank / invalid clears any override.
    private int? ResolveOverride()
    {
        if (int.TryParse(RespawnHoursText.Trim(), out int h) && h > 0 && h != _gameDataHours)
            return h;
        return null;
    }
}
