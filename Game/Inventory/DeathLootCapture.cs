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
    //
    // The lit light and the keys drop at a death too (GAME_MECHANICS "Deathpile —
    // where the items go"). The inventory read lists both apart from the pack, so
    // they are added here, under lost: the light is not gear to put back on (the
    // light engine lights one again when a room needs it), and a key goes back on
    // the ring by being picked up. A key-ring entry keeps its count, as a pack
    // entry does.
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) FromSnapshot(InventorySnapshot snapshot)
    {
        var equipped = new List<DeathItem>(snapshot.EquippedItems.Count);
        foreach (EquippedItem item in snapshot.EquippedItems)
            equipped.Add(new DeathItem(item.Name, item.Slot));

        var lost = new List<DeathItem>(snapshot.CarriedItems.Count);
        foreach (string entry in snapshot.CarriedItems)
            lost.Add(new DeathItem(entry));
        if (snapshot.ReadiedLight is { Name.Length: > 0 } light)
            lost.Add(new DeathItem(light.Name));
        if (snapshot.Keys is { } keys)
            foreach (string key in keys)
                if (!string.IsNullOrWhiteSpace(key)) lost.Add(new DeathItem(key, onKeyRing: true));

        return (equipped, lost);
    }
}
