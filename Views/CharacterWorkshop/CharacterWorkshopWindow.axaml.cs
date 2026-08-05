using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class CharacterWorkshopWindow : Window
{
    public CharacterWorkshopWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "workshop");

        // Tabs differ a lot in how much room they want — the Bosses grid needs
        // far more width (and height) than a form-style tab like Character Info.
        // Snap the window to the freshly-selected tab's content on every switch,
        // then hand sizing back to Manual so the user can still drag-resize until
        // the next switch. MinWidth/MinHeight (set in XAML) keep narrow tabs
        // comfortable; the window's own screen clamp caps how far a big tab grows.
        if (this.FindControl<TabControl>("SectionTabs") is { } tabs)
            tabs.SelectionChanged += OnSectionChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e) => FitToActiveTab();

    // Momentarily size-to-content so the window fits the active tab, then revert
    // to Manual (after the auto-size layout pass) to re-enable manual resize
    // without snapping back.
    private void FitToActiveTab()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        Dispatcher.UIThread.Post(() => SizeToContent = SizeToContent.Manual, DispatcherPriority.Loaded);
    }
}
