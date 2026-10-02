using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.StatusBar;

// One row of the status bar: its three zones, or — as a marquee — one crawling line
// made of every item's text.
public sealed partial class StatusBarRowViewModel : ObservableObject
{
    public IReadOnlyList<StatusBarItemViewModel> Left { get; }
    public IReadOnlyList<StatusBarItemViewModel> Center { get; }
    public IReadOnlyList<StatusBarItemViewModel> Right { get; }
    public bool IsMarquee { get; }

    [ObservableProperty] private string _marqueeText = string.Empty;

    public StatusBarRowViewModel(
        IReadOnlyList<StatusBarItemViewModel> left, IReadOnlyList<StatusBarItemViewModel> center,
        IReadOnlyList<StatusBarItemViewModel> right, bool isMarquee)
    {
        Left = left;
        Center = center;
        Right = right;
        IsMarquee = isMarquee;
    }

    public IEnumerable<StatusBarItemViewModel> Items => Left.Concat(Center).Concat(Right);
}
