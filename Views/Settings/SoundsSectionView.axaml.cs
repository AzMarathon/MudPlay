using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Sound for \"{row.Label}\"",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Sound files") { Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) row.CustomFile = path;
    }
}
