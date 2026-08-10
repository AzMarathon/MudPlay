using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One row of the Item Finder's trial gearset: a slot, the item currently placed in
// it, and a Hold lock that keeps Find Best from overwriting it. Options are the
// currently-filtered results that fit this slot (plus the sentinel empty option and
// whatever is already selected, so an out-of-filter pick still displays). The parent
// VM owns Options content and recomputes the trial totals via the change callback.
public sealed partial class TrialSlotRow : ObservableObject
{
    // The empty selection sentinel (a slot with nothing trialled). SelectedItem holds
    // this rather than null so the ComboBox has a concrete item to show.
    public const string Empty = "(none)";

    private readonly Action _onChanged;
    private bool _suppress;

    public EquipmentSlot Slot { get; }
    public string SlotLabel { get; }
    public ObservableCollection<string> Options { get; } = new() { Empty };

    // The chosen item, or Empty for none. Bound to the slot's dropdown.
    [ObservableProperty] private string? _selectedItem = Empty;
    // Locks this slot against Find Best.
    [ObservableProperty] private bool _hold;
    // Hover tooltip for the slot: the current item's stat lines (null when empty).
    [ObservableProperty] private string? _itemTooltip;

    public TrialSlotRow(EquipmentSlot slot, string slotLabel, Action onChanged)
    {
        Slot = slot;
        SlotLabel = slotLabel;
        _onChanged = onChanged;
    }

    // The real item name, or null when the slot is empty.
    public string? ItemName => string.IsNullOrEmpty(SelectedItem) || SelectedItem == Empty ? null : SelectedItem;

    // Assign an item (or null → Empty) without firing the change callback — used by
    // bulk operations (import / find best / clear) that recompute once at the end. A
    // pick outside the current filter is added to Options so the dropdown shows it.
    public void SetItemQuiet(string? item)
    {
        _suppress = true;
        try
        {
            string val = string.IsNullOrEmpty(item) ? Empty : item;
            if (val != Empty && !Options.Contains(val)) Options.Add(val);
            SelectedItem = val;
        }
        finally { _suppress = false; }
    }

    // Reconcile the dropdown options to (Empty sentinel + the given names + the
    // current pick) IN PLACE — never Clear(), because clearing an ObservableCollection
    // bound to a ComboBox nulls its SelectedItem and the pick renders blank until the
    // next redraw. The current pick is always kept in the desired set, so it's never
    // removed and the selection is undisturbed. Suppressed so the reconcile doesn't
    // fire the change callback.
    public void RebuildOptions(IReadOnlyList<string> names)
    {
        _suppress = true;
        try
        {
            var desired = new List<string>(names.Count + 2) { Empty };
            desired.AddRange(names);
            string? sel = SelectedItem;
            if (!string.IsNullOrEmpty(sel) && sel != Empty && !desired.Contains(sel)) desired.Add(sel);

            for (int i = Options.Count - 1; i >= 0; i--)
                if (!desired.Contains(Options[i])) Options.RemoveAt(i);
            foreach (string d in desired)
                if (!Options.Contains(d)) Options.Add(d);
        }
        finally { _suppress = false; }
    }

    partial void OnSelectedItemChanged(string? value) { if (!_suppress) _onChanged(); }
    partial void OnHoldChanged(bool value) { if (!_suppress) _onChanged(); }
}
