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

    public WorkshopItemRow(string name, string slot, int itemNumber)
    {
        Name = name;
        Slot = slot;
        CanOpen = itemNumber > 0;
        Open = new RelayCommand(
            () => { if (itemNumber > 0) _ = AppServices.Current.ItemRecord.OpenAsync(itemNumber); },
            () => CanOpen);
    }
}
