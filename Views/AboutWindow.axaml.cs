using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// Help → About: program identity, a clickable repo link, a tab per bundled
// license, and a community thank-you. Read-only modeless window (DataContext =
// AboutWindowViewModel); Close on the button or the toggle command.
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
