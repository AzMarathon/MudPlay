using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels.Settings;

namespace MudPlay.Views.Settings;

public partial class SoundsSectionView : UserControl
{
    public SoundsSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // The file picker needs the window, so it lives here rather than on the row.
    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SoundCueRowViewModel row }) return;
        if (await SoundFilePicker.PickAsync(this, $"Sound for \"{row.Label}\"") is { } path) row.CustomFile = path;
    }
}
