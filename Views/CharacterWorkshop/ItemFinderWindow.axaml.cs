using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class ItemFinderWindow : Window
{
    private ItemFinderViewModel? _vm;

    // Columns that read best low-to-high on the first click: the name/type text
    // and the slot order. Every other column here is a numeric stat where the
    // useful answer is "which item has the most", so those flip to descending
    // first (see OnGridSorting).
    private static readonly HashSet<string> _ascendingFirstPaths = new(StringComparer.Ordinal)
    {
        "Name", "TypeLabel", "SlotOrder",
    };

    // Lead column order for each layout, keyed by each column's identifier — the Tag
    // (binding path) for the stat columns, "slot" / "name" for the two untagged
    // anchors. The remaining columns trail in their declared order. An all-weapon
    // view fronts the weapon stats; an all-armour view fronts slot / defence.
    private static readonly string[] _armourLead =
        { "slot", "name", "TypeLabel", "LevelReqText", "EncumText", "AcText", "DrText" };
    private static readonly string[] _weaponLead =
        { "name", "TypeLabel", "LevelReqText", "StrReqText", "DamageText", "SwingSpeedText", "HitMagicText", "EncumText" };

    // Column lookup keyed as above, built once from the fixed column set.
    private Dictionary<string, DataGridColumn>? _columnByKey;

    // Stable key for the trial panel's slots-vs-stats split ratio (per-profile).
    private const string TrialSplitterId = "ItemFinderTrialSplit";

    public ItemFinderWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Right-click doesn't select a DataGrid row on its own, so the context menu's
        // "Trial-Equip this Item" (bound to SelectedItem) would target the wrong row.
        // Select the row under the cursor first, on the tunnelling pass so it lands
        // before the menu opens.
        ItemsGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        // Trial panel's slots pane (row 3) vs. stats pane (row 5) split — remembered
        // per profile so a hand-sized layout survives reopening the finder.
        AppServices.Current.SplitterLayouts.AttachGridRows(
            owner: this, grid: TrialGrid, topRowIndex: 3, bottomRowIndex: 5, id: TrialSplitterId);
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ItemsGrid).Properties.IsRightButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: ItemFinderEntry entry })
            ItemsGrid.SelectedItem = entry;
    }

    // Width the window grows/shrinks by when the trial flyout shows/hides — the
    // panel (340) plus the grid's column spacing (12) — so the results table keeps
    // its width instead of the panel eating into it.
    private const double TrialPanelWidth = 352;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ColumnLayoutChanged -= ApplyColumnLayout;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as ItemFinderViewModel;
        if (_vm is null) return;
        _vm.ColumnLayoutChanged += ApplyColumnLayout;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyColumnLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemFinderViewModel.ShowTrialPanel) && _vm is not null)
            Width += _vm.ShowTrialPanel ? TrialPanelWidth : -TrialPanelWidth;
    }

    // Map each column to its layout key once — the Tag for the stat columns, and the
    // two untagged anchors by their sort path (Slot / Name are the only Tag-less ones).
    private Dictionary<string, DataGridColumn> ColumnIndex()
    {
        if (_columnByKey is not null) return _columnByKey;
        _columnByKey = new Dictionary<string, DataGridColumn>(StringComparer.Ordinal);
        foreach (DataGridColumn col in ItemsGrid.Columns)
        {
            if (col.Tag is string tag) _columnByKey[tag] = col;
            else if (col.SortMemberPath == "SlotOrder") _columnByKey["slot"] = col;
            else if (col.SortMemberPath == "Name") _columnByKey["name"] = col;
        }
        return _columnByKey;
    }

    // Match the grid's columns to the current view: hide the all-blank stat columns,
    // reorder to front the columns that matter for the shown item kind, and drop the
    // Slot column in an all-weapon view (every weapon shares the Weapon slot).
    private void ApplyColumnLayout()
    {
        if (_vm is null) return;
        Dictionary<string, DataGridColumn> index = ColumnIndex();

        // Show a tagged stat column only while some visible row carries a value for
        // it, so a narrowed result set collapses its all-blank columns. Untagged
        // columns (Slot / Name) are the always-on anchors — left to the mode below.
        foreach (DataGridColumn col in ItemsGrid.Columns)
            if (col.Tag is string key)
                col.IsVisible = _vm.IsColumnVisible(key);

        // Slot is dead weight in an all-weapon view; an always-on anchor otherwise.
        bool weaponView = _vm.LayoutMode == ItemFinderLayout.Weapon;
        if (index.TryGetValue("slot", out DataGridColumn? slot))
            slot.IsVisible = !weaponView;

        string[]? lead = _vm.LayoutMode switch
        {
            ItemFinderLayout.Weapon => _weaponLead,
            ItemFinderLayout.Armour => _armourLead,
            _ => null,   // Mixed keeps the declared order.
        };
        ApplyColumnOrder(lead, index);
    }

    // Reorder the grid to lead with the mode's key columns, then the rest in their
    // declared order. Display indices are assigned in ascending target order: each
    // set pulls its column to slot i, shifting only the not-yet-placed columns (≥ i),
    // so the already-finalised prefix is never disturbed.
    private void ApplyColumnOrder(string[]? leadKeys, Dictionary<string, DataGridColumn> index)
    {
        List<DataGridColumn> order = new(ItemsGrid.Columns.Count);
        HashSet<DataGridColumn> placed = new();
        if (leadKeys is not null)
            foreach (string k in leadKeys)
                if (index.TryGetValue(k, out DataGridColumn? c) && placed.Add(c))
                    order.Add(c);
        foreach (DataGridColumn col in ItemsGrid.Columns)
            if (placed.Add(col))
                order.Add(col);

        for (int i = 0; i < order.Count; i++)
            if (order[i].DisplayIndex != i)
                order[i].DisplayIndex = i;
    }

    // Double-click a result → open that item's record (edit dialog) directly. A
    // double-tap also selects the row, so SelectedItem is the double-clicked entry.
    // The finder stays open (modeless) alongside the record.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is ItemFinderEntry entry && entry.Number > 0)
            _ = AppServices.Current.ItemRecord.OpenAsync(entry.Number);
    }

    // Take over sorting so numeric stat columns lead with their biggest values.
    // The DataGrid's built-in default is ascending-first, which buries the useful
    // items (the highest damage / AC / bonus) at the bottom and puts zeros and
    // negatives on top. We drive the CollectionView's SortDescriptions directly —
    // the header arrow follows that collection, so no per-column state to poke.
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (ItemsGrid.CollectionView is not { } view) return;
        string? path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        e.Handled = true;

        ListSortDirection firstClick = _ascendingFirstPaths.Contains(path)
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        DataGridSortDescription? existing = null;
        foreach (DataGridSortDescription d in view.SortDescriptions)
        {
            if (d.HasPropertyPath && d.PropertyPath == path) { existing = d; break; }
        }

        ListSortDirection next = existing is null
            ? firstClick
            : Opposite(existing.Direction);

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(path, next, (System.Globalization.CultureInfo?)null));

        static ListSortDirection Opposite(ListSortDirection d) =>
            d == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }
}
