namespace MudPlay.Game.Inventory;

// What EquipmentManager.UpdateSetFromWorn did.
public enum EquipUpdateOutcome
{
    // The set now holds the worn gear.
    Updated,

    // No set matched the name.
    NotFound,

    // A gear swap is streaming, so the worn list is mid-change — nothing written.
    Busy,

    // No `i` dump read yet — the worn list is unknown, and an empty one would wipe
    // the set. Nothing written.
    InventoryUnknown,
}
