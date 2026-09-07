using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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

    // Last-applied zone state, so a reflow only happens when it genuinely needs to and
    // preserves what the user dragged. _configExtent is the config pane's fixed size in
    // the current orientation (a row Height when vertical, a column Width when not).
    private bool _appliedShowConfig;
    private bool _appliedVertical;
    private double _configExtent;

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
        }
        ApplyZoneLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuffWatchdogViewModel.Layout)) ApplyZoneLayout();
    }

    private void OnBuffsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ShowPanel toggles the config zone (a non-caster has no configurable buffs);
        // reflow so the splitter + zone sizing collapse / restore to match — but ONLY
        // when it actually flips. It re-raises on every add / remove / toggle even when
        // the value is unchanged, and a reflow rebuilds the zone grid, which would snap
        // the splitter back to its default division and undo wherever the user dragged it.
        if (e.PropertyName == nameof(ViewModels.BuffPanelViewModel.ShowPanel)
            && (_vm?.Buffs?.ShowPanel ?? false) != _appliedShowConfig)
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
            int r = Grid.GetRow(_configZone);
            int c = Grid.GetColumn(_configZone);
            if (_appliedVertical && r >= 0 && r < _zonesGrid.RowDefinitions.Count)
                _configExtent = _zonesGrid.RowDefinitions[r].Height.Value;
            else if (!_appliedVertical && c >= 0 && c < _zonesGrid.ColumnDefinitions.Count)
                _configExtent = _zonesGrid.ColumnDefinitions[c].Width.Value;
        }

        _zonesGrid.RowDefinitions.Clear();
        _zonesGrid.ColumnDefinitions.Clear();

        bool showConfig = _vm?.Buffs?.ShowPanel ?? false;
        _appliedShowConfig = showConfig;
        if (!showConfig)
        {
            _zoneSplitter.IsVisible = false;
            _zonesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(_barsZone, 0);
            Grid.SetColumn(_barsZone, 0);
            return;
        }

        BuffWatchdogLayout layout = _vm?.Layout ?? BuffWatchdogLayout.ConfigTop;
        bool vertical = layout is BuffWatchdogLayout.ConfigTop or BuffWatchdogLayout.ConfigBottom;
        bool configFirst = layout is BuffWatchdogLayout.ConfigTop or BuffWatchdogLayout.ConfigLeft;

        // A preserved extent only applies within the same orientation; an orientation
        // flip falls back to the default starting division for that axis.
        double keptExtent = _appliedVertical == vertical && _configExtent > 0 ? _configExtent : 0;
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
        DetachVm();
        if (DataContext is ViewModels.BuffWatchdogViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
