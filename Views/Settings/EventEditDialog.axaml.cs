using Avalonia.Controls;
using Avalonia.Interactivity;
using MudPlay.ViewModels.Settings;

namespace MudPlay.Views.Settings;

public sealed partial class EventEditDialog : Window
{
    public EventEditDialog() => InitializeComponent();

    // The file picker needs the window, so it lives here rather than on the view model.
    private async void OnBrowseSound(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EventEditDialogViewModel vm) return;
        if (await SoundFilePicker.PickAsync(this, "Sound for this event") is { } path) vm.SoundFile = path;
    }
}
