namespace MudPlay.Models.Profile;

// One kind of item on a HeldAtDisconnect list: how many copies the character
// held, worn, carried, lit and on the key ring together.
public sealed class HeldItem
{
    // Bare item name as the inventory list prints it.
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; } = 1;

    public HeldItem() { }

    public HeldItem(string name, int count)
    {
        Name = name;
        Count = count;
    }
}
