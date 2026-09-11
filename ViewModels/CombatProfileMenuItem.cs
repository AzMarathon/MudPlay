using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One entry in the Action → Profiles fly-out and the toolbar's profile-menu
// button — a combat profile shown as "N) name", with the active one's row
// checked. Clicking switches to that profile. Rebuilt from CombatProfileManager
// whenever the profile list or active profile changes.
public sealed partial class CombatProfileMenuItem : ObservableObject
{
    // 1-based position — the user-facing profile number.
    public int Number { get; }

    public string Name { get; }

    // Menu label: "1) Fire", or "1) (unnamed)" for a blank name.
    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Number}) (unnamed)" : $"{Number}) {Name}";

    // True when this is the active profile — the row gets the checkmark, and the
    // chip fills with this profile's colour.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipBackground))]
    [NotifyPropertyChangedFor(nameof(ChipForeground))]
    [NotifyPropertyChangedFor(nameof(ChipBorder))]
    private bool _isActive;

    // This profile's distinct accent colour (by number) — every profile reads as
    // its own. Every chip is tinted its soft colour with its colour as the text, so
    // the profiles are visually distinct at rest; the ACTIVE chip additionally gets
    // a solid-colour ring (inactive chips a faint one) so it stands out.
    public IBrush AccentBrush => CombatProfilePalette.SolidBrush(Number);
    public IBrush AccentSoftBrush => CombatProfilePalette.SoftBrush(Number);
    public IBrush ChipBackground => AccentSoftBrush;
    public IBrush ChipForeground => AccentBrush;
    public IBrush ChipBorder => IsActive ? AccentBrush : AccentSoftBrush;

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
