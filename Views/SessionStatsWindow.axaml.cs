using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using MudPlay.ViewModels;

namespace MudPlay.Views;

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
        DataFormat.CreateInProcessFormat<string>("mudplay-session-stats-panel");

    // Thin accent line drawn on the DropOverlay canvas during a drag to preview the
    // drop position. On an overlay, not in the panel stack: slotted between panels it
    // shifted them as it moved — moving the very midpoints it's placed by, so it
    // flickered between gaps — and re-fit the window on every move.
    private readonly Border _dropIndicator = new()
    {
        Height = 3,
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
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "session-stats", autoHeight: true);
        Closed += OnClosed;

        _dropIndicator.Background =
            this.TryFindResource("AccentCyanBrush", out object? res) && res is IBrush brush
                ? brush
                : Brushes.DeepSkyBlue;

        if (this.FindControl<Grid>("PanelGrid") is { } grid)
        {
            // Tunnel so the title handle records the pressed panel before the
            // inner controls (expander headers, the Reset button) handle the click.
            grid.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
            grid.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
            grid.AddHandler(DragDrop.DragOverEvent, OnPanelDragOver);
            grid.AddHandler(DragDrop.DragLeaveEvent, OnPanelDragLeave);
            grid.AddHandler(DragDrop.DropEvent, OnPanelDrop);
            grid.LayoutUpdated += (_, _) => RefitToContent(grid);
        }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionStatsViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SessionStatsViewModel.Columns)) OnColumnsChanged(vm);
                };
        };

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
    // changes (a panel expanded / collapsed / hidden, or opening the window with its
    // saved set of expanded panels). Avalonia's SizeToContent
    // fits on open but doesn't reliably react to runtime content changes here, so
    // we drive the height ourselves: measure the whole body unbounded to learn the
    // height it wants, add the (constant) window chrome, clamp to Min/Max, and set
    // it. Guarded on the measured content height so the resize's own layout pass —
    // and the per-second stat refreshes — don't spin a loop.
    private void RefitToContent(Grid host)
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
        // Show everything that's open (user, 2026-09-30): the cap is the screen's
        // working area, not a fixed height, and a window that would run off the
        // bottom moves up to keep it all on screen. Only more than a screenful scrolls.
        Screen? screen = Screens.ScreenFromWindow(this);
        double ceiling = screen is null ? MaxHeight : Math.Min(MaxHeight, screen.WorkingArea.Height / screen.Scaling);
        double target = Math.Clamp(neededClient + chrome + BottomBuffer, MinHeight, Math.Max(MinHeight, ceiling));
        if (Math.Abs(Height - target) > 0.5) Height = target;
        if (screen is not null)
        {
            PixelRect area = screen.WorkingArea;
            int heightPx = (int)Math.Ceiling(target * screen.Scaling);
            if (Position.Y + heightPx > area.Bottom)
                Position = new PixelPoint(Position.X, Math.Max(area.Y, area.Bottom - heightPx));
        }
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
    // The panels live in one or two column hosts. Their order is one list; in two
    // columns the first SplitAt fill PanelHost and the rest PanelHost2. A drop picks
    // the column under the cursor and the gap by the panels' midpoints in it.

    private StackPanel[] Hosts() =>
        new[] { this.FindControl<StackPanel>("PanelHost"), this.FindControl<StackPanel>("PanelHost2") }
            .Where(h => h is not null).Select(h => h!).ToArray();

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
            HideDropIndicator();
        }
    }

    // The column host under the pointer (the visible one nearest it) and the index
    // among its panels the drop would land at.
    private (StackPanel Host, int Index, double GapY)? DropTarget(DragEventArgs e)
    {
        StackPanel? host = null;
        double best = double.MaxValue;
        foreach (StackPanel h in Hosts())
        {
            if (!h.IsVisible) continue;
            double x = e.GetPosition(h).X;
            double distance = x < 0 ? -x : x > h.Bounds.Width ? x - h.Bounds.Width : 0;
            if (distance < best) { best = distance; host = h; }
        }
        if (host is null) return null;

        double y = e.GetPosition(host).Y;
        int index = 0;
        Control? below = null, last = null;
        foreach (Control child in host.Children)
        {
            if (child.Tag is not string) continue;
            if (child.IsVisible)
            {
                last = child;
                if (below is null && y < child.Bounds.Y + child.Bounds.Height / 2) { below = child; break; }
            }
            index++;
        }
        double gapY = below is not null ? below.Bounds.Y - host.Spacing / 2
            : last is not null ? last.Bounds.Bottom + host.Spacing / 2
            : 0;
        return (host, index, gapY);
    }

    private void OnPanelDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(PanelFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;

        if (this.FindControl<Canvas>("DropOverlay") is not { } overlay || DropTarget(e) is not { } target) return;
        if (!overlay.Children.Contains(_dropIndicator)) overlay.Children.Add(_dropIndicator);
        _dropIndicator.Width = Math.Max(0, target.Host.Bounds.Width - 4);
        Canvas.SetLeft(_dropIndicator, target.Host.Bounds.X + 2);
        Canvas.SetTop(_dropIndicator, target.Host.Bounds.Y + target.GapY - _dropIndicator.Height / 2);
    }

    private void OnPanelDragLeave(object? sender, DragEventArgs e) => HideDropIndicator();

    private void HideDropIndicator()
    {
        if (this.FindControl<Canvas>("DropOverlay") is { } overlay)
            overlay.Children.Remove(_dropIndicator);
    }

    private void OnPanelDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SessionStatsViewModel vm) return;
        if (e.DataTransfer.TryGetValue(PanelFormat) is not { } draggedId) return;
        HideDropIndicator();
        if (DropTarget(e) is not { } target) return;

        StackPanel[] hosts = Hosts();
        List<string> left = TagsIn(hosts[0]);
        List<string> right = hosts.Length > 1 ? TagsIn(hosts[1]) : new();
        List<string> into = target.Host == hosts[0] ? left : right;
        int index = target.Index;
        int old = into.IndexOf(draggedId);
        if (old >= 0 && old < index) index--;
        left.Remove(draggedId);
        right.Remove(draggedId);
        into.Insert(Math.Clamp(index, 0, into.Count), draggedId);

        List<string> order = left.Concat(right).ToList();
        int split = vm.IsTwoColumns ? left.Count : vm.SplitAt;
        vm.SaveOrder(order, split);
        ApplyLayout(vm);
    }

    // Lay the panels out in the VM's order and column count.
    private void ApplySavedOrder()
    {
        if (DataContext is SessionStatsViewModel vm)
        {
            SetGridColumns(vm.IsTwoColumns);
            ApplyLayout(vm);
        }
    }

    // Going to two columns widens the window to fit them, and back narrows it, so
    // each column keeps the width one had.
    private const double ColumnGap = 8;
    private void OnColumnsChanged(SessionStatsViewModel vm)
    {
        SetGridColumns(vm.IsTwoColumns);
        Width = vm.IsTwoColumns ? Width * 2 + ColumnGap : Math.Max(MinWidth, (Width - ColumnGap) / 2);
        ApplyLayout(vm);
    }

    private void SetGridColumns(bool two)
    {
        if (this.FindControl<Grid>("PanelGrid") is not { } grid) return;
        grid.ColumnDefinitions = two
            ? new ColumnDefinitions($"*,{ColumnGap},*")
            : new ColumnDefinitions("*,0,0");
    }

    private void ApplyLayout(SessionStatsViewModel vm)
    {
        StackPanel[] hosts = Hosts();
        if (hosts.Length == 0) return;
        IReadOnlyList<string> order = vm.PanelOrder;
        int[] placed = new int[hosts.Length];
        for (int i = 0; i < order.Count; i++)
        {
            if (PanelWithTag(hosts, order[i]) is not { } panel) continue;
            int col = vm.IsTwoColumns && hosts.Length > 1 && i >= vm.SplitAt ? 1 : 0;
            StackPanel to = hosts[col];
            if (panel.Parent is StackPanel from && from != to) from.Children.Remove(panel);
            int at = placed[col]++;
            int cur = to.Children.IndexOf(panel);
            if (cur < 0) to.Children.Insert(at, panel);
            else if (cur != at) to.Children.Move(cur, at);
        }
    }

    // Walk up from the event source to the nearest element flagged as a drag
    // handle (a panel title); stop at a column host so a press elsewhere yields false.
    private static bool IsOnDragHandle(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null and not StackPanel { Name: "PanelHost" or "PanelHost2" }; e = e.Parent)
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

    private static Control? PanelWithTag(IEnumerable<StackPanel> hosts, string id)
    {
        foreach (StackPanel host in hosts)
            foreach (Control child in host.Children)
                if (child.Tag as string == id)
                    return child;
        return null;
    }

    private static List<string> TagsIn(StackPanel host)
    {
        List<string> ids = new();
        foreach (Control child in host.Children)
            if (child.Tag is string id)
                ids.Add(id);
        return ids;
    }
}
