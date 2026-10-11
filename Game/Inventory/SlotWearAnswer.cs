namespace MudPlay.Game.Inventory;

// The game's answer to a slot claim's wear (EquipmentManager.SlotWearAnswered).
public enum SlotWearAnswer
{
    // "You are now wearing <item>."
    Worn,

    // "You may not wear that item!": class, level or alignment.
    Refused,

    // "<item> may not be worn!": the item has no wear slot.
    CannotBeWorn,

    // "You have no more room to wear that item!": true until a piece comes off.
    NoRoom,

    // "You do not have <item> left unequipped.": it isn't in the pack.
    NotInPack,

    // "You are already wearing <other> and it may not be removed.": the slot's
    // occupant can't come off.
    OccupantStuck,
}
