namespace MudPlay.Game.Inventory;

// What became of a claim on a gear slot (EquipmentManager.ClaimSlot). A caller
// that holds a step for the game's answer waits only on Sent and OnItsWay.
public enum SlotClaimResult
{
    // The item isn't wearable gear, or is neither worn nor in the pack.
    NotClaimed,

    // The item is on already; the slot is held.
    AlreadyWorn,

    // The wear went out.
    Sent,

    // Another owner already sent this same item's wear; the slot is shared.
    OnItsWay,

    // A more pressing owner holds the slot with another piece. The slot is
    // claimed and nothing was sent.
    Waiting,

    // A sneak is being kept: the wear is queued for when the hold lifts.
    Held,

    // The send gate is locked, so the command would have been dropped. Nothing
    // is claimed.
    NotSent,
}
