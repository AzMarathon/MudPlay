using Avalonia.Controls;
using MudPlay.ViewModels;

namespace MudPlay.Views;

public sealed partial class GmcpDataWindow : Window
{
    public GmcpDataWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as GmcpDataViewModel)?.Dispose();
    }
}
