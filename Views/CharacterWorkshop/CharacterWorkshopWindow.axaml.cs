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

    private CharacterWorkshopViewModel? _vm;

    // The tab the window was last sized for by its own PreferredSize, and that size.
    private WorkshopSectionViewModel? _sizedFor;
    private Avalonia.Size _sizedTo;

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
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // A sub-tab switch, or a tab changing the size it asks for (the Item Finder
    // showing its Gear Finder panel), re-fits the same way a main-tab switch does.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.ActiveLayoutChanged -= FitToActiveTab;
        _vm = DataContext as CharacterWorkshopViewModel;
        if (_vm is not null) _vm.ActiveLayoutChanged += FitToActiveTab;
    }

    // The remembered layout puts the window back where it was, at the size it had
    // when it last closed, which was whatever tab was showing then. The tab it
    // opens on now gets its own fit, as it would on a switch to it; the position
    // stays as remembered. base raises Opened, which is where the layout is restored.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToActiveTab();
    }

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles: an inner ComboBox / ListBox (e.g. the Monster
        // Matchup pickers) would otherwise re-fit — and so resize — the whole window
        // on every pick (report paradigm-20260813-125617). Only the section
        // TabControl's own selection change should trigger a re-fit.
        if (!ReferenceEquals(e.Source, sender)) return;
        FitToActiveTab();
    }

    // Fit the window to the active tab, then revert to Manual (after the layout
    // pass) so manual resize works without snapping back. Width fits the tab's
    // content; height is the Equipment tab's height — the Equipment tab sizes
    // both dimensions to itself and its rendered height becomes the reference
    // every other tab uses, so long lists (Quest, Bosses) scroll instead of
    // growing the window taller than Equipment. A tab that names its own size
    // (WorkshopSectionViewModel.PreferredSize) gets that instead.
    private void FitToActiveTab()
    {
        // A maximized window has its size from the screen. Asking it to fit a tab
        // laid the content out past the screen's edge.
        if (WindowState != WindowState.Normal) return;

        WorkshopSectionViewModel? active = _vm?.ActiveSection;
        if (active?.PreferredSize is { } wanted)
        {
            SizeToContent = SizeToContent.Manual;
            // The tab already on screen asking for a different size (its side panel
            // opened or closed) moves the window by the difference, so a size the
            // user dragged it to isn't thrown away. Arriving on the tab takes the
            // size as asked.
            Avalonia.Size target = ReferenceEquals(active, _sizedFor)
                ? new Avalonia.Size(
                    Bounds.Width + wanted.Width - _sizedTo.Width,
                    Bounds.Height + wanted.Height - _sizedTo.Height)
                : wanted;
            (Width, Height) = FitToScreen(target);
            _sizedFor = active;
            _sizedTo = wanted;
            return;
        }
        _sizedFor = null;

        bool isEquipment = active?.Id == EquipmentSectionViewModel.SectionId;
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

    // A tab that changed the size it asks for while the window was maximized is
    // owed the difference once the window is back at its own size. Posted: the
    // window manager is still putting the restored size back when the state flips.
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || WindowState != WindowState.Normal) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.ActiveSection is { } active && ReferenceEquals(active, _sizedFor)
                && active.PreferredSize is { } wanted && wanted != _sizedTo)
                FitToActiveTab();
        }, DispatcherPriority.Background);
    }

    // A size a tab asked for, held to what the screen the window is on can show.
    private (double Width, double Height) FitToScreen(Avalonia.Size wanted)
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return (wanted.Width, wanted.Height);
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        return (Math.Min(wanted.Width, screen.WorkingArea.Width / scale),
                Math.Min(wanted.Height, screen.WorkingArea.Height / scale));
    }
}
