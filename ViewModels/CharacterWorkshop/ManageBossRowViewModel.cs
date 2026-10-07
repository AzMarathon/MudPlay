using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

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
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _inStock;
    [ObservableProperty] private bool _inParadigm;
    [ObservableProperty] private bool _showInTable = true;
    // What the Bosses tab's "Reset to default" puts Stop before / Grab All back to.
    [ObservableProperty] private bool _defaultStopBefore = true;
    [ObservableProperty] private bool _defaultGrabAll;

    public int? MonsterNumber { get; private set; }
    // What the game calls the boss when its name here is a label (BossDef.GameName).
    // Not edited in the dialog; carried through so a save doesn't drop it.
    private readonly string _gameName = string.Empty;
    // The live flags are edited on the main table and carried through unchanged; a
    // boss added here starts on its defaults.
    private readonly bool _isNew;
    private readonly bool _stopBefore;
    private readonly bool _grabAll;

    public ManageBossRowViewModel() { _isNew = true; }

    public ManageBossRowViewModel(BossDef def, int? gameDataHours)
    {
        _gameDataHours = gameDataHours;
        Name = def.Name;
        Rooms = BossRoomText.Format(def.Rooms);
        int? shown = def.RespawnHoursOverride ?? gameDataHours;
        RespawnHoursText = shown?.ToString() ?? string.Empty;
        Notes = def.Notes;
        IsCleanup = def.RespawnType == BossRespawnType.Cleanup;
        _stopBefore = def.StopBefore;
        _grabAll = def.GrabAll;
        DefaultStopBefore = def.ResetStopBefore;
        DefaultGrabAll = def.ResetGrabAll;
        InStock = def.InStock;
        InParadigm = def.InParadigm;
        ShowInTable = def.ShowInTable;
        MonsterNumber = def.MonsterNumber;
        _gameName = def.GameName;
    }

    public BossDef ToDef() => new()
    {
        Name = Name.Trim().ToLowerInvariant(),
        GameName = _gameName,
        MonsterNumber = MonsterNumber,
        Rooms = BossRoomText.Parse(Rooms),
        InStock = InStock,
        InParadigm = InParadigm,
        RespawnType = IsCleanup ? BossRespawnType.Cleanup : BossRespawnType.Timed,
        StopBefore = _isNew ? DefaultStopBefore : _stopBefore,
        GrabAll = _isNew ? DefaultGrabAll : _grabAll,
        DefaultStopBefore = DefaultStopBefore,
        DefaultGrabAll = DefaultGrabAll,
        RespawnHoursOverride = ResolveOverride(),
        Notes = Notes.Trim(),
        ShowInTable = ShowInTable,
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
