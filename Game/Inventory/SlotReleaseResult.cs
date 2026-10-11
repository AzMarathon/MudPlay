namespace MudPlay.Game.Inventory;

// What became of letting a gear slot go (EquipmentManager.ClearSlotOverride).
public enum SlotReleaseResult
{
    // The owner had no claim on the slot.
    NotOwned,

    // A sneak is being kept, so the revert waits. The claim still stands.
    Held,

    // The claim is gone and the piece stays where it is.
    Released,

    // The claim is gone and the commands that take the piece off went out.
    ReleasedWithCommands,
}
