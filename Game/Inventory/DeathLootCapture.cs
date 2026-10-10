using MudPlay.Models.Profile;

namespace MudPlay.Game.Inventory;

// Splits an InventorySnapshot into the two halves a deathpile is recorded as:
// items worn at death (re-equippable) and carried-but-unworn items ("inventory
// lost"). Shared by RoomTracker.NoteDeath (real deaths) and
// DeathRecoveryManager.SimulateDeath (the test button) so both capture
// identically.
public static class DeathLootCapture
{
    // Map a snapshot to (equipped, lost): every worn piece with its slot, and every
    // pack entry as the pack holds it, count and all. A pack entry named like a
    // worn piece is a spare, and is lost with the rest: InventoryManager takes a
    // piece out of the pack as it is worn and puts it back as it comes off, so the
    // two lists never hold the same copy and nothing here is dropped by name.
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) FromSnapshot(InventorySnapshot snapshot)
    {
        var equipped = new List<DeathItem>(snapshot.EquippedItems.Count);
        foreach (EquippedItem item in snapshot.EquippedItems)
            equipped.Add(new DeathItem(item.Name, item.Slot));

        var lost = new List<DeathItem>(snapshot.CarriedItems.Count);
        foreach (string entry in snapshot.CarriedItems)
            lost.Add(new DeathItem(entry));

        return (equipped, lost);
    }
}
