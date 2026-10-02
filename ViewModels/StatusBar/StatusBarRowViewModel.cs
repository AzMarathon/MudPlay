using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.StatusBar;

// One row of the status bar: its three zones, or — as a marquee — one crawling line
// made of its text items, with the chip, the statline warning and the connection
// light held still at the ends.
public sealed partial class StatusBarRowViewModel : ObservableObject
{
    public IReadOnlyList<StatusBarItemViewModel> Left { get; }
    public IReadOnlyList<StatusBarItemViewModel> Center { get; }
    public IReadOnlyList<StatusBarItemViewModel> Right { get; }
    public bool IsMarquee { get; }

    // In a marquee row the items that aren't text stay put: those from the left and
    // centre at the left end, those from the right at the right end.
    public IReadOnlyList<StatusBarItemViewModel> PinnedLeft { get; }
    public IReadOnlyList<StatusBarItemViewModel> PinnedRight { get; }

    [ObservableProperty] private string _marqueeText = string.Empty;

    public StatusBarRowViewModel(
        IReadOnlyList<StatusBarItemViewModel> left, IReadOnlyList<StatusBarItemViewModel> center,
        IReadOnlyList<StatusBarItemViewModel> right, bool isMarquee)
    {
        Left = left;
        Center = center;
        Right = right;
        IsMarquee = isMarquee;
        PinnedLeft = left.Concat(center).Where(static i => !i.IsText).ToList();
        PinnedRight = right.Where(static i => !i.IsText).ToList();
    }

    public IEnumerable<StatusBarItemViewModel> Items => Left.Concat(Center).Concat(Right);
}
