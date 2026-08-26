namespace MudPlay.ViewModels.CharacterWorkshop;

// One row of the Roomba Master List: an item Roomba has seen, the room it was
// seen in, and where it can be sold/bought (non-gang-house shops only).
public sealed class RoombaMasterListRowViewModel
{
    public string ItemName { get; }
    public int Quantity { get; }
    public string SeenIn { get; }
    public string Market { get; }

    public RoombaMasterListRowViewModel(string itemName, int quantity, string seenIn, string market)
    {
        ItemName = itemName;
        Quantity = quantity;
        SeenIn = seenIn;
        Market = market;
    }
}
