using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One entry in the Action → Profiles fly-out and the toolbar's profile-menu
// button — a casting spell profile shown as "N) name", with the active one's row
// checked. Clicking switches to that profile. Rebuilt from CombatProfileManager
// whenever the profile list or active profile changes.
public sealed partial class CombatProfileMenuItem : ObservableObject
{
    // 1-based position — the user-facing profile number.
    public int Number { get; }

    public string Name { get; }

    // Menu label: "1) Fire", or "1) (unnamed)" for a blank name.
    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Number}) (unnamed)" : $"{Number}) {Name}";

    // True when this is the active profile — the row gets the checkmark.
    [ObservableProperty] private bool _isActive;

    // Switches to this profile.
    public ICommand SwitchCommand { get; }

    public CombatProfileMenuItem(int number, string name, bool isActive, ICommand switchCommand)
    {
        Number = number;
        Name = name ?? string.Empty;
        IsActive = isActive;
        SwitchCommand = switchCommand;
    }
}
