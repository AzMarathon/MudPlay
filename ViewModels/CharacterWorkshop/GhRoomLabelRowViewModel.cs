using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One labeled gang-house room in the GH Management tab's list. Read-only
// display + a Remove button — editing a label re-opens the same right-click
// picker the map uses (RenameFavorite mirrors this "map is the editor"
// pattern for favourites), so this row doesn't duplicate the picker UI.
public sealed partial class GhRoomLabelRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    public string RoomName { get; }
    public string RoomKeyText => Key.ToString();
    public string CategoryText { get; }

    // Live sweep status for this room: "Scanning" during recon, "Cleaning" while it
    // still has items to move out, "Complete" once cleared (or after a run). Blank
    // before the first sweep. Written by GhManagementSectionViewModel.
    [ObservableProperty] private string _status = string.Empty;

    private readonly Action<GhRoomLabelRowViewModel> _onRemove;

    public GhRoomLabelRowViewModel(GhRoomLabel label, string? roomName, Action<GhRoomLabelRowViewModel> onRemove)
    {
        ArgumentNullException.ThrowIfNull(onRemove);
        Key = new RoomKey(label.Map, label.Room);
        RoomName = string.IsNullOrWhiteSpace(roomName) ? "(unknown)" : roomName;
        _onRemove = onRemove;

        string rules = label.Rules.Count == 0
            ? "(no rules)"
            : string.Join("; ", label.Rules.Select(DescribeRule));
        CategoryText = label.IsCatchAll ? $"{rules} [catch-all]" : rules;
    }

    private static string DescribeRule(GhCategoryRule rule)
    {
        if (rule.Worn is int worn)
            return "Slot: " + (LookupEnums.FormatWornSlot(worn) ?? "Unknown");

        string category = LookupEnums.FormatItemType(rule.ItemType?.ToString()) ?? "Unknown";
        if (rule.WeaponType is { } wt)
            category += " > " + (LookupEnums.FormatWeaponType(wt.ToString()) ?? "Unknown");
        else if (rule.ArmourType is { } at)
            category += " > " + (LookupEnums.FormatArmourType(at.ToString()) ?? "Unknown");
        return category;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
