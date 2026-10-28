using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels.Guides;

namespace MudPlay.Views;

// Help → Visual Guides: the guide picker (DataContext = GuidePickerViewModel).
// Modeless; it closes itself when a guide is opened so the step card and the
// windows the guide points at aren't covered.
public partial class GuidePickerWindow : Window, MudPlay.Services.IShowsNoGameData
{
    public GuidePickerWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GuidePickerViewModel vm) vm.CloseRequested += Close;
        };
        Closed += (_, _) =>
        {
            if (DataContext is GuidePickerViewModel vm)
            {
                vm.CloseRequested -= Close;
                vm.Detach();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
