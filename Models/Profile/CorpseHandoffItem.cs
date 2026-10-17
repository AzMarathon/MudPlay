namespace MudPlay.Models.Profile;

// One item name taken out of another player's corpse, with how many copies came
// out, as the game's `You took <item>.` lines named it.
public sealed class CorpseHandoffItem
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }

    public CorpseHandoffItem() { }

    public CorpseHandoffItem(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public override string ToString() => Count > 1 ? $"{Count} {Name}" : Name;
}
