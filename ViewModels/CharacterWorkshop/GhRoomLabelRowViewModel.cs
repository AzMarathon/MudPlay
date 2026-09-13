using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One SORT RULE of a labeled gang-house room in the Roomba tab's list — a room
// with three rules shows as three rows, so a rule can be edited or dropped on
// its own instead of taking the room's whole rule set with it. A room with no
// explicit rules (a pure catch-all) still gets one row, RuleIndex -1, so it
// stays visible, manageable and removable.
public sealed partial class GhRoomLabelRowViewModel : ObservableObject
{
    public RoomKey Key { get; }

    // Index into the room label's rule list; -1 for a room that has no rules.
    public int RuleIndex { get; }

    public string RoomName { get; }
    public string RoomKeyText => Key.ToString();
    public string CategoryText { get; }

    // Live sweep status for this room: "Scanning" during recon, "Cleaning" while it
    // still has items to move out, "Complete" once cleared (or after a run). Blank
    // before the first sweep. Written by GhManagementSectionViewModel.
    [ObservableProperty] private string _status = string.Empty;

    // "Actively Manage" checkbox: whether Start Sweep / Start Inventory visits this
    // room for THIS character. Two-way bound; a user toggle writes back through
    // _onManageToggle to the per-character GhManagedRoomStore. Sourced from that
    // store (not the shared label), so alts on the same BBS manage independently;
    // rooms adopted via @roomba sync arrive unchecked. Every row of the same room
    // carries the same tick — the section VM keeps the siblings in step.
    [ObservableProperty] private bool _activelyManaged;

    private readonly Action<RoomKey, bool> _onManageToggle;
    private readonly Action<RoomKey> _onGoto;
    // Guards the ctor's initial assignment from writing back to the store — rows are
    // rebuilt on every label/managed-set change, so a write there would loop.
    private readonly bool _loaded;

    public GhRoomLabelRowViewModel(GhRoomLabel label, int ruleIndex, string? roomName, bool activelyManaged,
        Action<RoomKey, bool> onManageToggle, Action<RoomKey> onGoto)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(onManageToggle);
        ArgumentNullException.ThrowIfNull(onGoto);
        Key = new RoomKey(label.Map, label.Room);
        RuleIndex = ruleIndex;
        RoomName = string.IsNullOrWhiteSpace(roomName) ? "(unknown)" : roomName;
        _onManageToggle = onManageToggle;
        _onGoto = onGoto;

        string rule = ruleIndex >= 0 && ruleIndex < label.Rules.Count
            ? DescribeRule(label.Rules[ruleIndex])
            : "(no rules)";
        // The catch-all flag belongs to the room, not the rule, so it repeats on each
        // of the room's rows — a sorted grid otherwise hides it on whichever row drifts.
        CategoryText = label.IsCatchAll ? $"{rule} [catch-all]" : rule;

        _activelyManaged = activelyManaged;
        _loaded = true;
    }

    partial void OnActivelyManagedChanged(bool value)
    {
        if (!_loaded) return;
        _onManageToggle(Key, value);
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

    // Queue + start a walk-to this room (the full "Walk here" path). Handed up to the
    // section VM, which routes it through AppServices.GoWalkTo.
    [RelayCommand]
    private void Goto() => _onGoto(Key);
}
