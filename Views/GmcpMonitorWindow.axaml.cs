using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Tools → GMCP Monitor. Code-behind holds the save picker, as the Wire Inspector's
// export does, and keeps the latest frame in view.
public partial class GmcpMonitorWindow : Window
{
    public GmcpMonitorWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "gmcpmonitor");
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GmcpMonitorViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(GmcpMonitorViewModel.Frames))
                        this.FindControl<ScrollViewer>("FrameScroll")?.ScrollToEnd();
                };
        };
        Closed += (_, _) => (DataContext as GmcpMonitorViewModel)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GmcpMonitorViewModel vm) return;
        TextBlock? result = this.FindControl<TextBlock>("ExportResult");
        string? text = vm.BuildExport(out string? error);
        if (text is null)
        {
            if (result is not null)
            {
                result.Text = error;
                result.IsVisible = true;
            }
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export GMCP recording",
            SuggestedFileName = $"gmcp-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl",
            DefaultExtension = "jsonl",
            FileTypeChoices = [new FilePickerFileType("GMCP recording (.jsonl)") { Patterns = ["*.jsonl"] }],
        });
        if (file is null) return;

        await using Stream stream = await file.OpenWriteAsync();
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(text);
        if (result is not null)
        {
            result.Text = $"Saved {file.Name}.";
            result.IsVisible = true;
        }
    }
}
