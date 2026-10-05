using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.Navigation;

// One hold or errand trip shown as a chip after the Navigation status line. A chip
// whose hold has ended stays on screen, faded out by the view, so a hold that lasted
// a split second can still be read.
public sealed partial class NavHoldChip(string label, NavChipTone tone, Func<string?>? tip = null) : ObservableObject
{
    public string Label { get; } = label;

    // Hover text, read fresh each time it is asked for; null shows no tooltip.
    public string? Tip => tip?.Invoke();

    // The figures behind Tip moved on.
    public void RefreshTip() => OnPropertyChanged(nameof(Tip));

    // A trip gets its own colour; a hold reads amber.
    public bool IsTrip { get; } = tone == NavChipTone.Trip;

    [ObservableProperty] private bool _isCleared;
}
