using Avalonia.Controls;

namespace MudPlay.Views.Navigation;

// The MegaMUD .mp import review — the decoded file beside our translation.
public partial class MpImportReviewWindow : Window
{
    public MpImportReviewWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MudPlay.ViewModels.Navigation.MpImportReviewViewModel)?.OnWindowClosed();
    }
}
