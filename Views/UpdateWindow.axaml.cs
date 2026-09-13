using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Help / Tools → Update the Client. Modeless window (DataContext = UpdateWindowViewModel);
// closing it cancels any in-flight check/download so a dismissed dialog doesn't
// keep working in the background.
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        Closed += (_, _) => (DataContext as UpdateWindowViewModel)?.Cancel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
