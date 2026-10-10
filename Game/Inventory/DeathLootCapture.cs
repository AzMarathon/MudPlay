using MudPlay.Models.Profile;

namespace MudPlay.Game.Inventory;

// What a death takes from an InventorySnapshot, as the two halves a deathpile is
// recorded as: items worn at death (re-equippable) and everything else that drops
// ("inventory lost"). The one rule for every death record: a death that was seen
// (RoomTracker.NoteDeath), the test button (DeathRecoveryManager.SimulateDeath) and
// one worked out afterwards from the list kept at a disconnect (HangupDeath.Pile,
// which starts from LostOf) all build their pile here, so they agree.
public static class DeathLootCapture
{
    // Map a snapshot to (equipped, lost): every worn piece with its slot, and what
    // LostOf lists.
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) FromSnapshot(InventorySnapshot snapshot)
    {
        var equipped = new List<DeathItem>(snapshot.EquippedItems.Count);
        foreach (EquippedItem item in snapshot.EquippedItems)
            equipped.Add(new DeathItem(item.Name, item.Slot));
        return (equipped, LostOf(snapshot));
    }

    // What a death takes that isn't worn: every pack entry as the pack holds it,
    // count and all, then the keys, then the lit light.
    //
    // A pack entry named like a worn piece is a spare, and is lost with the rest:
    // InventoryManager takes a piece out of the pack as it is worn and puts it back
    // as it comes off, so the two lists never hold the same copy and nothing here
    // is dropped by name. So a single spare of a worn piece is recorded like any
    // pack copy (user, 2026-10-09).
    //
    // The keys and the lit light drop at a death too (GAME_MECHANICS "Death
    // threshold & consequences"). The inventory read lists both apart from the
    // pack. A key-ring entry keeps its count and is flagged: the game treats a key
    // on the ring differently from the same item in the pack. The light is not gear
    // to put back on (the light engine lights one again when a room needs it).
    public static List<DeathItem> LostOf(InventorySnapshot snapshot)
    {
        var lost = new List<DeathItem>(snapshot.CarriedItems.Count);
        foreach (string entry in snapshot.CarriedItems)
            if (!string.IsNullOrWhiteSpace(entry)) lost.Add(new DeathItem(entry));
        foreach (string key in snapshot.Keys ?? Array.Empty<string>())
        {
            (int count, string name) = InventorySnapshot.ParseKeyEntry(key);
            if (name.Length > 0) lost.Add(new DeathItem(count > 1 ? $"{count} {name}" : name, onKeyRing: true));
        }
        if (snapshot.ReadiedLight is { Name.Length: > 0 } light)
            lost.Add(new DeathItem(light.Name));
        return lost;
    }
}
