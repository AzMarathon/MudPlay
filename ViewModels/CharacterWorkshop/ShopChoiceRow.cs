namespace MudPlay.ViewModels.CharacterWorkshop;

// One alternate shop offered in an item's "sell somewhere else" popup: the shop's
// name, plus its map/room and current walking distance folded into one detail line.
public sealed class ShopChoiceRow
{
    public int Shop { get; }
    public string ShopName { get; }
    public string Detail { get; }   // "map/room · N steps" (or just "map/room")

    public ShopChoiceRow(int shop, string shopName, string location, string steps)
    {
        Shop = shop;
        ShopName = shopName;
        Detail = steps.Length > 0 ? $"{location} · {steps}" : location;
    }
}
