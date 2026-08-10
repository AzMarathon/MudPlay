namespace MudPlay.Models.Profile;

// The 21 equipment slots an EquipmentSet can control, in the Workshop's display
// order. The order mirrors what a player sees when looking at someone in game —
// worn slots top-to-bottom (Head → Feet → Worn), then the weapon block at the
// bottom (Off-Hand, then Weapon), so the Equipment Manager and Item Finder trial
// slots read the same way as an in-game "look". The two Alternate slots follow
// their primaries in the same pairing (Alt Off-Hand, then Alt Weapon); they are
// virtual — applying a set never sends a wire wear for them; instead it writes
// the matching CombatSettings.AlternateWeapon / CombatSettings.AlternateOffHand
// so the combat weapon-swap matrix picks them up. The remaining slots map to real
// worn-item placements (see Game.Inventory.EquipmentSlotMap for the MajorMUD
// worn-id behind each).
//
// Reordering is safe: slots persist by NAME (JsonStringEnumConverter), and the
// only numeric use, ItemFinderEntry.SlotOrder = (int)Slot, is a display sort. The
// paired-slot "primary wins" resolution (EquipmentSlotMap.SlotForItem walks
// DisplayOrder = this enum's order) only requires each primary to precede its
// pair-mate — Wrist1<Wrist2, Finger1<Finger2, OffHand<AltOffHand, Weapon<AltWeapon
// — which this order keeps.
public enum EquipmentSlot
{
    Head,
    Ears,
    Eyes,
    Face,
    Neck,
    Back,
    Torso,
    Arms,
    Wrist1,
    Wrist2,
    Hands,
    Finger1,
    Finger2,
    Waist,
    Legs,
    Feet,
    Worn,
    OffHand,
    Weapon,
    AlternateOffHand,
    AlternateWeapon,
}
