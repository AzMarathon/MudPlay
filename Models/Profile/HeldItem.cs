namespace MudPlay.Models.Profile;

// One kind of item on a HeldAtDisconnect list: how many copies the character
// held, worn, carried, lit and on the key ring together, and the slot of each
// copy that was worn.
public sealed class HeldItem
{
    // Bare item name as the inventory list prints it.
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; } = 1;

    // Worn-slot label of each worn copy (the Game.Inventory.EquippedItem.Slot
    // vocabulary), so a copy picked back up goes on with the right verb. null
    // when no copy was worn.
    public List<string>? WornSlots { get; set; }

    public HeldItem() { }

    public HeldItem(string name, int count, List<string>? wornSlots = null)
    {
        Name = name;
        Count = count;
        WornSlots = wornSlots;
    }
}
