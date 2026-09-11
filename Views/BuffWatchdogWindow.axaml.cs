using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Buff Watchdog window. Bound to ViewModels.BuffWatchdogViewModel;
// code-behind attaches the persisted window-layout + the global-hotkeys handler (so
// chord forwards still work when this window has focus), disposes the VM on close,
// and arranges the two zones (config table + timer bars) around a drag splitter per
// the layout chosen in Settings → General. The layout lives in the VM (reloaded live
// on a Settings Apply), so we reflow whenever VM.Layout — or the config panel's
// visibility — changes rather than binding a fixed dock in XAML.
public partial class BuffWatchdogWindow : Window
{
    private readonly Grid? _zonesGrid;
    private readonly Control? _configZone;
    private readonly GridSplitter? _zoneSplitter;
    private readonly Control? _barsZone;

    private BuffWatchdogViewModel? _vm;
    private INotifyPropertyChanged? _buffsNotifier;

    // Manual pointer drag-to-reorder of the config buff rows. A drag starts ONLY
    // from a row's grip handle (so it doesn't hijack the inline checkboxes, the
    // horizontal scroll, or double-click-to-edit) and shows a live insertion line.
    // Manual (not DragDrop) so it's repeatable and can paint where the row will land.
    private ItemsControl? _buffRows;
    private BuffSlotRowViewModel? _dragRow;
    private bool _dragActive;
    private Point _dragStart;

    // Last-applied zone state, so a reflow only happens when it genuinely needs to and
    // preserves what the user dragged. _configExtent is the config pane's fixed size (a
    // row Height when vertical, a column Width when not); _configExtentVertical is the
    // orientation it's for, so a persisted height isn't reused as a width after a layout
    // change. Seeded from the profile on open, saved back on close (per character).
    private bool _appliedShowConfig;
    private bool _appliedVertical;
    private double _configExtent;
    private bool _configExtentVertical;

    public BuffWatchdogWindow()
    {
        InitializeComponent();
        _zonesGrid    = this.FindControl<Grid>("ZonesGrid");
        _configZone   = this.FindControl<Control>("ConfigZone");
        _zoneSplitter = this.FindControl<GridSplitter>("ZoneSplitter");
        _barsZone     = this.FindControl<Control>("BarsZone");

        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "buffwatchdog");
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;

        _buffRows = this.FindControl<ItemsControl>("BuffRowsList");
        if (_buffRows is { } rows)
        {
            // Tunnel so the grip press is seen before the row's inner controls.
            rows.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
            rows.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel);
            rows.AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    // ----- drag-to-reorder of the config buff rows (manual pointer) ---
    // The pointer is captured to the rows list on a grip press, and the target row
    // is hit-tested by POINTER POSITION each move (not e.Source, which during a drag
    // stays on the pressed element and made the insertion line stick to the source
    // row). That lets the row land anywhere, including between rows below it.

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left button, and ONLY when the press starts on a row's grip handle.
        if (_buffRows is null
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || !StartedOnGrip(e.Source as StyledElement))
        {
            _dragRow = null;
            return;
        }
        _dragRow = RowOf(e.Source as StyledElement);
        _dragStart = e.GetPosition(_buffRows);
        _dragActive = false;
        if (_dragRow is not null) e.Pointer.Capture(_buffRows);
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null || _buffRows is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { e.Pointer.Capture(null); ClearDrag(); return; }

        Point now = e.GetPosition(_buffRows);
        if (!_dragActive
            && Math.Abs(now.X - _dragStart.X) < 4 && Math.Abs(now.Y - _dragStart.Y) < 4)
            return;
        _dragActive = true;
        _dragRow.IsDragging = true;
        PaintInsertion(RowUnder(now));
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only act when a grip-drag is actually in progress (a grip press set
        // _dragRow and captured the pointer). This is a TUNNEL handler on the whole
        // rows list, so it fires for every release in the panel — a checkbox / button
        // / ✎ click included. Releasing the pointer capture unconditionally cancelled
        // that control's OWN capture before it could register its click (Avalonia
        // flips IsPressed off on capture-loss), which silently broke every inline
        // checkbox and button. A non-grip press never captured, so there's nothing to
        // release — leave the click alone.
        if (_dragRow is null) return;
        if (_dragActive && _dragRow is { } src && _buffRows is not null && _vm?.Buffs is { } buffs
            && RowUnder(e.GetPosition(_buffRows)) is { } hit)
            buffs.MoveRowToIndex(src, hit.bottom ? hit.index + 1 : hit.index);
        e.Pointer.Capture(null);
        ClearDrag();
    }

    // Clear the drag state + every row's insertion / dragging indicator.
    private void ClearDrag()
    {
        if (_dragRow is not null) _dragRow.IsDragging = false;
        _dragRow = null;
        _dragActive = false;
        if (_vm?.Buffs is { } buffs)
            foreach (BuffSlotRowViewModel r in buffs.Slots) { r.DropAbove = false; r.DropBelow = false; }
    }

    // Paint the insertion line at the boundary the pointer picked.
    private void PaintInsertion((BuffSlotRowViewModel row, int index, bool bottom)? hit)
    {
        if (_vm?.Buffs is not { } buffs) return;
        foreach (BuffSlotRowViewModel r in buffs.Slots) { r.DropAbove = false; r.DropBelow = false; }
        if (hit is not { } h) return;
        if (!h.bottom) h.row.DropAbove = true;
        else if (h.index == buffs.Slots.Count - 1) h.row.DropBelow = true;
        else buffs.Slots[h.index + 1].DropAbove = true;
    }

    // The buff row whose realized container contains posInList's Y (or the nearest
    // row when the pointer is above the first / below the last), + whether the pointer
    // is in that row's bottom half. Hit-tested by geometry so it tracks the pointer
    // regardless of which element the (captured) event reports as its source.
    private (BuffSlotRowViewModel row, int index, bool bottom)? RowUnder(Point posInList)
    {
        if (_buffRows is null || _vm?.Buffs is not { } buffs) return null;
        BuffSlotRowViewModel? nearest = null;
        int nearestIndex = -1;
        double nearestBottom = double.MaxValue;
        bool nearestIsBottomHalf = false;
        foreach (Control c in _buffRows.GetRealizedContainers())
        {
            if (c.DataContext is not BuffSlotRowViewModel row) continue;
            if (c.TranslatePoint(new Point(0, 0), _buffRows) is not { } tl) continue;
            int i = buffs.Slots.IndexOf(row);
            if (i < 0) continue;
            double top = tl.Y, h = c.Bounds.Height, mid = top + h / 2;
            if (posInList.Y >= top && posInList.Y < top + h)
                return (row, i, posInList.Y >= mid);
            double dist = Math.Abs(posInList.Y - mid);
            if (dist < nearestBottom)
            {
                nearestBottom = dist;
                nearest = row;
                nearestIndex = i;
                nearestIsBottomHalf = posInList.Y >= mid;
            }
        }
        // Above the first / below the last row → snap to the nearest row's edge.
        return nearest is null ? null : (nearest, nearestIndex, nearestIsBottomHalf);
    }

    // True when the pressed element (or an ancestor) is a row's drag grip.
    private static bool StartedOnGrip(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
            if (e is Control { Tag: "dragHandle" }) return true;
        return false;
    }

    // Walk up from the event source to the nearest buff-row DataContext.
    private static BuffSlotRowViewModel? RowOf(StyledElement? src)
    {
        for (StyledElement? e = src; e is not null; e = e.Parent)
            if (e.DataContext is BuffSlotRowViewModel row) return row;
        return null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachVm();
        if (DataContext is BuffWatchdogViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            if (vm.Buffs is INotifyPropertyChanged buffs)
            {
                _buffsNotifier = buffs;
                buffs.PropertyChanged += OnBuffsPropertyChanged;
            }
            // Seed the splitter position from the character's saved value so the window
            // reopens at the same division the user last dragged it to.
            _configExtent = vm.ConfigExtent;
            _configExtentVertical = vm.ConfigExtentVertical;
        }
        ApplyZoneLayout();
    }

    // The config zone shows only when the class HAS configurable buffs (ShowPanel) and
    // the user hasn't collapsed it with the timer-bar-side toggle (ConfigCollapsed).
    private bool EffectiveShowConfig =>
        (_vm?.Buffs?.ShowPanel ?? false) && !(_vm?.ConfigCollapsed ?? false);

    // The splitter's fixed thickness (kept in step with ApplyZoneLayout's 4px splitter).
    private const double SplitterThickness = 4;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuffWatchdogViewModel.Layout))
            ApplyZoneLayout();
        else if (e.PropertyName == nameof(BuffWatchdogViewModel.ConfigCollapsed))
            ToggleConfigWithResize();
    }

    // Collapsing / expanding the config panel also resizes the WINDOW along the split
    // axis, so the panel's space is handed back on collapse and reclaimed on expand —
    // the divider edge lands where the separator was, no manual resize needed. Keeps
    // the top-left corner fixed (the far edge moves), which is exactly right for the
    // config-on-right / config-on-bottom layouts and harmless for the others.
    private void ToggleConfigWithResize()
    {
        bool collapsing = !EffectiveShowConfig;
        ApplyZoneLayout();   // captures the config extent on collapse, restores it on expand
        bool vertical = _vm?.Layout is BuffWatchdogLayout.ConfigTop or BuffWatchdogLayout.ConfigBottom;
        double extent = _configExtent > 0 ? _configExtent : (vertical ? 180 : 300);
        double delta = extent + SplitterThickness;
        if (vertical)
            Height = collapsing ? System.Math.Max(MinHeight, Height - delta) : Height + delta;
        else
            Width = collapsing ? System.Math.Max(MinWidth, Width - delta) : Width + delta;
    }

    private void OnBuffsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ShowPanel toggles the config zone (a non-caster has no configurable buffs);
        // reflow so the splitter + zone sizing collapse / restore to match — but ONLY
        // when the effective visibility actually flips. It re-raises on every add /
        // remove / toggle even when unchanged, and a reflow rebuilds the zone grid, which
        // would snap the splitter back to its default division and undo the user's drag.
        if (e.PropertyName == nameof(ViewModels.BuffPanelViewModel.ShowPanel)
            && EffectiveShowConfig != _appliedShowConfig)
            ApplyZoneLayout();
    }

    // Rebuild the zone grid: config table + timer bars split by a draggable
    // GridSplitter, oriented per the chosen layout. Both content zones are star-sized
    // so they fill the window; with no configurable buffs the bars take everything.
    private void ApplyZoneLayout()
    {
        if (_zonesGrid is null || _configZone is null || _zoneSplitter is null || _barsZone is null)
            return;

        // Capture the size the user dragged the config pane to before we clear the grid,
        // so a legitimate reflow (panel show/hide, layout orientation change) re-applies
        // it instead of snapping back to the default division. Only meaningful when the
        // pane was showing in the SAME orientation — a height can't carry to a width.
        if (_appliedShowConfig)
        {
            double cur = CurrentConfigExtent();
            if (cur > 0) { _configExtent = cur; _configExtentVertical = _appliedVertical; }
        }

        _zonesGrid.RowDefinitions.Clear();
        _zonesGrid.ColumnDefinitions.Clear();

        // Config shows only when the class has buffs to configure AND the user hasn't
        // collapsed the panel with the bar-side toggle. Own the zone + splitter visibility
        // here (no XAML IsVisible binding) so the two can't fight this reflow.
        bool showConfig = EffectiveShowConfig;
        _appliedShowConfig = showConfig;
        _configZone.IsVisible = showConfig;
        if (!showConfig)
        {
            _zoneSplitter.IsVisible = false;
            _zonesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            // Reset EVERY child to the single cell (0,0). The config zone and splitter
            // keep whatever row/column they were assigned in the expanded layout (1, 2,
            // …); collapsing to one cell leaves those indices pointing past the grid, and
            // Avalonia's Grid.MeasureCellsGroup then indexes its definition arrays out of
            // range and crashes — even though those two are IsVisible=false.
            Grid.SetRow(_configZone, 0);
            Grid.SetColumn(_configZone, 0);
            Grid.SetRow(_zoneSplitter, 0);
            Grid.SetColumn(_zoneSplitter, 0);
            Grid.SetRow(_barsZone, 0);
            Grid.SetColumn(_barsZone, 0);
            return;
        }

        BuffWatchdogLayout layout = _vm?.Layout ?? BuffWatchdogLayout.ConfigTop;
        bool vertical = layout is BuffWatchdogLayout.ConfigTop or BuffWatchdogLayout.ConfigBottom;
        bool configFirst = layout is BuffWatchdogLayout.ConfigTop or BuffWatchdogLayout.ConfigLeft;

        // A preserved extent (dragged this session or restored from the profile) only
        // applies within the same orientation; an orientation flip falls back to the
        // default starting division for that axis.
        double keptExtent = _configExtentVertical == vertical && _configExtent > 0 ? _configExtent : 0;
        _appliedVertical = vertical;

        _zoneSplitter.IsVisible = true;

        // The config table holds a FIXED size while the timer bars flex (star), so
        // resizing the window only grows / shrinks the bars — the splitter stays put
        // where the user dragged it instead of drifting with the window. Dragging the
        // splitter re-sizes the fixed config pane; min sizes stop either zone
        // collapsing. Defaults below are just the starting division.
        if (vertical)
        {
            var configDef = new RowDefinition(new GridLength(keptExtent > 0 ? keptExtent : 180)) { MinHeight = 70 };
            var barsDef = new RowDefinition(GridLength.Star) { MinHeight = 70 };
            var splitDef = new RowDefinition(GridLength.Auto);
            if (configFirst)
            {
                _zonesGrid.RowDefinitions.Add(configDef);
                _zonesGrid.RowDefinitions.Add(splitDef);
                _zonesGrid.RowDefinitions.Add(barsDef);
                Grid.SetRow(_configZone, 0);
                Grid.SetRow(_zoneSplitter, 1);
                Grid.SetRow(_barsZone, 2);
            }
            else
            {
                _zonesGrid.RowDefinitions.Add(barsDef);
                _zonesGrid.RowDefinitions.Add(splitDef);
                _zonesGrid.RowDefinitions.Add(configDef);
                Grid.SetRow(_barsZone, 0);
                Grid.SetRow(_zoneSplitter, 1);
                Grid.SetRow(_configZone, 2);
            }
            Grid.SetColumn(_configZone, 0);
            Grid.SetColumn(_barsZone, 0);
            Grid.SetColumn(_zoneSplitter, 0);

            _zoneSplitter.Height = 4;
            _zoneSplitter.Width = double.NaN;
            _zoneSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _zoneSplitter.VerticalAlignment = VerticalAlignment.Center;
            _zoneSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            var configDef = new ColumnDefinition(new GridLength(keptExtent > 0 ? keptExtent : 300)) { MinWidth = 140 };
            var barsDef = new ColumnDefinition(GridLength.Star) { MinWidth = 120 };
            var splitDef = new ColumnDefinition(GridLength.Auto);
            if (configFirst)
            {
                _zonesGrid.ColumnDefinitions.Add(configDef);
                _zonesGrid.ColumnDefinitions.Add(splitDef);
                _zonesGrid.ColumnDefinitions.Add(barsDef);
                Grid.SetColumn(_configZone, 0);
                Grid.SetColumn(_zoneSplitter, 1);
                Grid.SetColumn(_barsZone, 2);
            }
            else
            {
                _zonesGrid.ColumnDefinitions.Add(barsDef);
                _zonesGrid.ColumnDefinitions.Add(splitDef);
                _zonesGrid.ColumnDefinitions.Add(configDef);
                Grid.SetColumn(_barsZone, 0);
                Grid.SetColumn(_zoneSplitter, 1);
                Grid.SetColumn(_configZone, 2);
            }
            Grid.SetRow(_configZone, 0);
            Grid.SetRow(_barsZone, 0);
            Grid.SetRow(_zoneSplitter, 0);

            _zoneSplitter.Width = 4;
            _zoneSplitter.Height = double.NaN;
            _zoneSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            _zoneSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            _zoneSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
    }

    // The config pane's current fixed extent (row height when vertical, column width
    // when not), or 0 when the config zone isn't laid out right now. Read live so a
    // splitter drag — which doesn't reflow the grid — is still captured (e.g. on close).
    private double CurrentConfigExtent()
    {
        if (_zonesGrid is null || _configZone is null || !_appliedShowConfig) return 0;
        if (_appliedVertical)
        {
            int r = Grid.GetRow(_configZone);
            return r >= 0 && r < _zonesGrid.RowDefinitions.Count ? _zonesGrid.RowDefinitions[r].Height.Value : 0;
        }
        int c = Grid.GetColumn(_configZone);
        return c >= 0 && c < _zonesGrid.ColumnDefinitions.Count ? _zonesGrid.ColumnDefinitions[c].Width.Value : 0;
    }

    // Double-click a buff row → open the same edit dialog the ✎ button opens
    // (spell, recast, and — for a mana-regen roll spell like profane link — its
    // reroll target), without needing to hit the small button precisely.
    private void OnBuffRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BuffSlotRowViewModel row } && _vm?.Buffs is { } buffs)
            buffs.EditBuffCommand.Execute(row);
    }

    private void DetachVm()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_buffsNotifier is not null) _buffsNotifier.PropertyChanged -= OnBuffsPropertyChanged;
        _vm = null;
        _buffsNotifier = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Persist the splitter position (a live drag doesn't reflow, so read it now).
        // Collapsed → no live def, and _configExtent already holds the last-shown value.
        if (_vm is { } vm)
        {
            double cur = CurrentConfigExtent();
            if (cur > 0) { _configExtent = cur; _configExtentVertical = _appliedVertical; }
            vm.SaveConfigExtent(_configExtent, _configExtentVertical);
        }
        DetachVm();
        if (DataContext is ViewModels.BuffWatchdogViewModel dvm) dvm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
