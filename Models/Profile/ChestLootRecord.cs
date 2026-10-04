using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// The Chest Offload list as last saved for the character: what the chests opened
// gave that hasn't been sold, dropped or cleared yet, and the coin they gave. Kept
// on the profile so closing the window (or the client) doesn't lose the list while
// the loot is still in the pack. See ChestOpenTracker.
public sealed class ChestLootRecord
{
    public List<ChestLootEntry> Items { get; set; } = new();
    public int Copper { get; set; }
    public int Silver { get; set; }
    public int Gold { get; set; }
    public int Platinum { get; set; }
    public int Runic { get; set; }
    public long TotalCopper { get; set; }
}

public sealed record ChestLootEntry(string Name, int Count);
