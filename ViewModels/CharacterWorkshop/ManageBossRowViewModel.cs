using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One editable boss entry in the Manage Bosses dialog. All fields are editable; a
// new row starts blank (visible on whichever realm the dialog was opened for).
public sealed partial class ManageBossRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _rooms = string.Empty;   // "map/room; map/room"
    [ObservableProperty] private bool _isCleanup;
    [ObservableProperty] private bool _exactSpawn;
    [ObservableProperty] private bool _stopBefore;
    [ObservableProperty] private bool _inStock;
    [ObservableProperty] private bool _inParadigm;

    public int? MonsterNumber { get; private set; }

    public ManageBossRowViewModel() { }

    public ManageBossRowViewModel(BossDef def)
    {
        Name = def.Name;
        Rooms = BossRoomText.Format(def.Rooms);
        IsCleanup = def.RespawnType == BossRespawnType.Cleanup;
        ExactSpawn = def.ExactSpawn;
        StopBefore = def.StopBefore;
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
        StopBefore = StopBefore,
    };
}
