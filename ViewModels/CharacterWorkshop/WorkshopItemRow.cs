using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One equipped or carried item in the Character Info panel — its name (and slot,
// for worn items), plus a link that opens the item's Game Data record. CanOpen is
// false when the name didn't resolve to an Items Number (inventory-dump names can
// be truncated / pluralised), in which case the view shows it as plain text.
public sealed class WorkshopItemRow
{
    public string Name { get; }
    public string Slot { get; }
    public bool CanOpen { get; }
    public ICommand Open { get; }

    // Charge readout for a limited-use item, e.g. "5 Charges" — shown after the name
    // in the panel. Empty for items with no known charge count (unlimited, not looked,
    // or a stock realm that prints none). Set from ItemChargeTracker.
    public string Charges { get; }
    public bool HasCharges => Charges.Length > 0;

    public WorkshopItemRow(string name, string slot, int itemNumber, string charges = "")
    {
        Name = name;
        Slot = slot;
        Charges = charges;
        CanOpen = itemNumber > 0;
        Open = new RelayCommand(
            () => { if (itemNumber > 0) _ = AppServices.Current.ItemRecord.OpenAsync(itemNumber); },
            () => CanOpen);
    }
}
