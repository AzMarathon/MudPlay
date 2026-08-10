using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class CharacterWorkshopWindow : Window
{
    // Every tab auto-sizes its WIDTH to its own content, but takes its HEIGHT
    // from the Equipment tab — the Quest and Bosses lists would otherwise balloon
    // the window far taller than the form-style tabs. Seeded with a comfortable
    // fallback until the Equipment tab is shown once and we learn its real height.
    private const double FallbackReferenceHeight = 640;
    private double _referenceHeight = FallbackReferenceHeight;

    public CharacterWorkshopWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "workshop");

        // Tabs differ a lot in how much width they want — the Bosses grid needs
        // far more than a form-style tab like Character Info. Snap the window to
        // the freshly-selected tab on every switch, then hand sizing back to
        // Manual so the user can still drag-resize until the next switch.
        if (this.FindControl<TabControl>("SectionTabs") is { } tabs)
            tabs.SelectionChanged += OnSectionChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e) => FitToActiveTab();

    // Fit the window to the active tab, then revert to Manual (after the layout
    // pass) so manual resize works without snapping back. Width fits the tab's
    // content; height is the Equipment tab's height — the Equipment tab sizes
    // both dimensions to itself and its rendered height becomes the reference
    // every other tab uses, so long lists (Quest, Bosses) scroll instead of
    // growing the window taller than Equipment.
    private void FitToActiveTab()
    {
        bool isEquipment = (DataContext as CharacterWorkshopViewModel)?.SelectedSection?.Id == "equipment";
        if (isEquipment)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Width;
            Height = _referenceHeight;
        }
        Dispatcher.UIThread.Post(() =>
        {
            // Learn the Equipment tab's real (styled, laid-out) height so the
            // other tabs match it.
            if (isEquipment && Bounds.Height > 0) _referenceHeight = Bounds.Height;
            SizeToContent = SizeToContent.Manual;
        }, DispatcherPriority.Loaded);
    }
}
