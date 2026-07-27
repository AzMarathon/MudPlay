using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless Session Stats window. Bound to SessionStatsViewModel;
// code-behind attaches the persisted window-layout, wires the global-hotkeys
// handler, disposes the VM on close, and hosts the panel drag-reorder gesture.
// A panel is dragged by its title label; an insertion line previews where it
// will land, and the VM's saved order is applied on open and pushed back on drop
// via SessionStatsViewModel.SaveOrder.
public partial class SessionStatsWindow : Window
{
    // In-process carrier for the dragged panel's Tag id. Avalonia 12's
    // DataTransfer surface replaced the legacy string-keyed DataObject.
    private static readonly DataFormat<string> PanelFormat =
        DataFormat.CreateInProcessFormat<string>("fujin-session-stats-panel");

    // Thin accent line slotted between panels during a drag to preview the drop
    // position. Non-hit-testable so it never intercepts the drag's hit-testing.
    private readonly Border _dropIndicator = new()
    {
        Height = 3,
        Margin = new Thickness(2, 0),
        CornerRadius = new CornerRadius(1.5),
        IsHitTestVisible = false,
    };

    // The panel id under the press point, captured on pointer-down (only when the
    // press lands on a title handle) and promoted to a drag past the threshold.
    private string? _pressedId;
    private Point _pressOrigin;

    // DoDragDropAsync needs the originating PointerPressedEventArgs; we detect the
    // drag in PointerMoved, so hold the press args.
    private PointerPressedEventArgs? _pressArgs;

    // Last panel-content height we re-fit the window to. SizeToContent="Height"
    // sizes the window on open but doesn't reliably shrink it when a panel is
    // collapsed / hidden at runtime, so we re-trigger it whenever the stacked
    // panels' desired height changes (guarded to avoid a layout loop).
    private double _lastContentHeight = -1;

    public SessionStatsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        // autoHeight: the window sizes its height to its visible content
        // (SizeToContent="Height"), so the layout store must not pin a saved height.
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "session-stats", autoHeight: true);
        Closed += OnClosed;

        _dropIndicator.Background =
            this.TryFindResource("AccentCyanBrush", out object? res) && res is IBrush brush
                ? brush
                : Brushes.DeepSkyBlue;

        if (this.FindControl<StackPanel>("PanelHost") is { } host)
        {
            // Tunnel so the title handle records the pressed panel before the
            // inner controls (expander headers, the Reset button) handle the click.
            host.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
            host.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
            host.AddHandler(DragDrop.DragOverEvent, OnPanelDragOver);
            host.AddHandler(DragDrop.DragLeaveEvent, OnPanelDragLeave);
            host.AddHandler(DragDrop.DropEvent, OnPanelDrop);
            host.LayoutUpdated += (_, _) => RefitToContent(host);
        }

        // Show the HP/MA graph's scrub cursor while the step slider is held. Tunnel
        // + handledEventsToo so the thumb's own pointer handling doesn't hide the
        // press/release from us; capture-lost covers a drag that ends off-thumb.
        if (this.FindControl<Slider>("StepSlider") is { } slider)
        {
            slider.AddHandler(PointerPressedEvent, OnSliderPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.AddHandler(PointerReleasedEvent, OnSliderReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            slider.AddHandler(PointerCaptureLostEvent, OnSliderCaptureLost);
        }

        // Apply the saved order once the children have materialised.
        Opened += (_, _) => ApplySavedOrder();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ----- Auto-fit height to the stacked panels ---------------------

    // Re-fit the window height to its content whenever the panels' total height
    // changes (a panel expanded / collapsed / hidden). Avalonia's SizeToContent
    // fits on open but doesn't reliably react to runtime content changes here, so
    // we drive the height ourselves: measure the whole body unbounded to learn the
    // height it wants, add the (constant) window chrome, clamp to Min/Max, and set
    // it. Guarded on the measured content height so the resize's own layout pass —
    // and the per-second stat refreshes — don't spin a loop.
    private void RefitToContent(StackPanel host)
    {
        double contentH = host.DesiredSize.Height;
        if (contentH <= 0 || Math.Abs(contentH - _lastContentHeight) < 1) return;
        _lastContentHeight = contentH;

        if (Content is not Control body || ClientSize.Height <= 0) return;

        double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        body.Measure(new Size(width, double.PositiveInfinity));
        double neededClient = body.DesiredSize.Height;

        // Height is the outer frame, ClientSize the inner area; the delta is the
        // chrome (title bar / borders), constant regardless of content. A small
        // bottom buffer keeps the last panel off the window's bottom edge.
        const double BottomBuffer = 5;
        double chrome = Math.Max(0, Height - ClientSize.Height);
        double target = Math.Clamp(neededClient + chrome + BottomBuffer, MinHeight, MaxHeight);
        if (Math.Abs(Height - target) > 0.5) Height = target;
    }

    // ----- HP/MA graph scrub cursor ---------------------------------

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => SetScrubbing(true);
    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e) => SetScrubbing(false);
    private void OnSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => SetScrubbing(false);

    private void SetScrubbing(bool on)
    {
        if (DataContext is SessionStatsViewModel vm) vm.IsScrubbing = on;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SessionStatsViewModel vm) vm.Dispose();
    }

    // ----- Panel drag-reorder ---------------------------------------

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left-button press on a title handle only. A click that doesn't move
        // never starts a drag, so a section title still toggles its expander and
        // right-click still opens the show/hide menu.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsOnDragHandle(e.Source as StyledElement))
        {
            _pressedId = null;
            _pressArgs = null;
            return;
        }
        _pressedId = PanelIdOf(e.Source as StyledElement);
        _pressOrigin = e.GetPosition(this);
        _pressArgs = e;
    }

    private async void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedId is null || _pressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedId = null;
            _pressArgs = null;
            return;
        }
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressOrigin.X) < 4 && Math.Abs(now.Y - _pressOrigin.Y) < 4)
            return;

        string id = _pressedId;
        PointerPressedEventArgs trigger = _pressArgs;
        _pressedId = null;
        _pressArgs = null;

        StackPanel? host = this.FindControl<StackPanel>("PanelHost");
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PanelFormat, id));
        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally
        {
            // Drop fires before the await returns; this also clears the preview
            // when the drag is cancelled or released outside the host.
            host?.Children.Remove(_dropIndicator);
        }
    }

    private void OnPanelDragOver(object? sender, DragEventArgs e)
    {
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;
        if (!e.DataTransfer.Contains(PanelFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;

        // Slot the insertion line into the gap nearest the cursor.
        host.Children.Remove(_dropIndicator);
        int insertAt = host.Children.Count;
        double y = e.GetPosition(host).Y;
        for (int i = 0; i < host.Children.Count; i++)
        {
            Control child = host.Children[i];
            if (child.Tag is not string || !child.IsVisible) continue;
            if (y < child.Bounds.Y + child.Bounds.Height / 2)
            {
                insertAt = i;
                break;
            }
        }
        host.Children.Insert(insertAt, _dropIndicator);
    }

    private void OnPanelDragLeave(object? sender, DragEventArgs e)
    {
        if (this.FindControl<StackPanel>("PanelHost") is { } host)
            host.Children.Remove(_dropIndicator);
    }

    private void OnPanelDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is not { } host) return;
        if (e.DataTransfer.TryGetValue(PanelFormat) is not { } draggedId) return;

        host.Children.Remove(_dropIndicator);

        List<string> ids = OrderedTags(host);
        int oldIndex = ids.IndexOf(draggedId);
        if (oldIndex < 0) return;

        // Count the visible panels whose midpoint sits above the cursor — that's
        // the gap the dragged panel lands in. Removing it first shifts the gap
        // down by one when the drag was originally above the target.
        int gap = 0;
        double y = e.GetPosition(host).Y;
        foreach (Control child in host.Children)
        {
            if (child.Tag is not string || !child.IsVisible) continue;
            if (y >= child.Bounds.Y + child.Bounds.Height / 2) gap++;
            else break;
        }

        ids.RemoveAt(oldIndex);
        int insertIndex = Math.Clamp(gap > oldIndex ? gap - 1 : gap, 0, ids.Count);
        ids.Insert(insertIndex, draggedId);

        ApplyOrder(host, ids);
        vm.SaveOrder(ids);
    }

    // Reorder the panel host's children to match the VM's saved order.
    private void ApplySavedOrder()
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (this.FindControl<StackPanel>("PanelHost") is { } host)
            ApplyOrder(host, vm.PanelOrder);
    }

    private static void ApplyOrder(StackPanel host, IReadOnlyList<string> ids)
    {
        for (int target = 0; target < ids.Count; target++)
        {
            Control? panel = PanelWithTag(host, ids[target]);
            if (panel is null) continue;
            int cur = host.Children.IndexOf(panel);
            if (cur >= 0 && cur != target)
                host.Children.Move(cur, target);
        }
    }

    // Walk up from the event source to the nearest element flagged as a drag
    // handle (a panel title); stop at the host so a press elsewhere yields false.
    private static bool IsOnDragHandle(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null and not StackPanel { Name: "PanelHost" }; e = e.Parent)
            if (e.Classes.Contains("draghandle"))
                return true;
        return false;
    }

    // Nearest ancestor (or self) carrying a string Tag — the panel id.
    private static string? PanelIdOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
            if (e is Control { Tag: string id })
                return id;
        return null;
    }

    private static Control? PanelWithTag(StackPanel host, string id)
    {
        foreach (Control child in host.Children)
            if (child.Tag as string == id)
                return child;
        return null;
    }

    private static List<string> OrderedTags(StackPanel host)
    {
        List<string> ids = new();
        foreach (Control child in host.Children)
            if (child.Tag is string id)
                ids.Add(id);
        return ids;
    }
}
