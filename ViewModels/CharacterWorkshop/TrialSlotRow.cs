using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

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

    // Replace the dropdown options (Empty sentinel + the given names) while keeping
    // the current pick selected and present, so a filter refresh never silently
    // clears a trialled slot. Suppressed so the reset doesn't fire the callback.
    public void RebuildOptions(IReadOnlyList<string> names)
    {
        string? keep = SelectedItem;
        _suppress = true;
        try
        {
            Options.Clear();
            Options.Add(Empty);
            foreach (string n in names) Options.Add(n);
            if (keep is not null && keep != Empty && !Options.Contains(keep)) Options.Add(keep);
            SelectedItem = keep is not null && Options.Contains(keep) ? keep : Empty;
        }
        finally { _suppress = false; }
    }

    partial void OnSelectedItemChanged(string? value) { if (!_suppress) _onChanged(); }
    partial void OnHoldChanged(bool value) { if (!_suppress) _onChanged(); }
}
