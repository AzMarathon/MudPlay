using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One editable row in the Settings → Other location-equip list. Wraps a single
// LocationEquipRule and notifies the parent section (markDirty) on every field
// change, so the tab's Save / Discard state tracks row edits.
public sealed partial class LocationEquipRuleViewModel : ObservableObject
{
    private readonly Action _markDirty;

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _mapRoomNumbers = "";
    [ObservableProperty] private LocationEquipMatchMode _match = LocationEquipMatchMode.Or;
    [ObservableProperty] private string _roomNameContains = "";
    [ObservableProperty] private string _itemName = "";

    public LocationEquipRuleViewModel(LocationEquipRule rule, Action markDirty)
    {
        _markDirty = markDirty ?? (static () => { });
        // Direct field assignment (not the generated setters) so seeding a row on
        // load doesn't fire markDirty.
        _enabled = rule.Enabled;
        _mapRoomNumbers = rule.MapRoomNumbers ?? "";
        _match = rule.Match;
        _roomNameContains = rule.RoomNameContains ?? "";
        _itemName = rule.ItemName ?? "";
    }

    // The And/Or choices for the row's dropdown (Or first — the default).
    public static LocationEquipMatchMode[] MatchModes { get; } =
        { LocationEquipMatchMode.Or, LocationEquipMatchMode.And };

    public LocationEquipRule ToModel() => new()
    {
        Enabled = Enabled,
        MapRoomNumbers = MapRoomNumbers?.Trim() ?? "",
        Match = Match,
        RoomNameContains = RoomNameContains?.Trim() ?? "",
        ItemName = ItemName?.Trim() ?? "",
    };

    partial void OnEnabledChanged(bool value) => _markDirty();
    partial void OnMapRoomNumbersChanged(string value) => _markDirty();
    partial void OnMatchChanged(LocationEquipMatchMode value) => _markDirty();
    partial void OnRoomNameContainsChanged(string value) => _markDirty();
    partial void OnItemNameChanged(string value) => _markDirty();
}
