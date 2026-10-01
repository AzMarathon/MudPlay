using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.Navigation;

// One hold or errand trip shown as a chip after the Navigation status line. A chip
// whose hold has ended stays on screen, faded out by the view, so a hold that lasted
// a split second can still be read.
public sealed partial class NavHoldChip(string label, NavChipTone tone) : ObservableObject
{
    public string Label { get; } = label;

    // A trip gets its own colour; a hold reads amber.
    public bool IsTrip { get; } = tone == NavChipTone.Trip;

    [ObservableProperty] private bool _isCleared;
}
