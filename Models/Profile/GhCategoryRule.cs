namespace MudPlay.Models.Profile;

// One matching rule inside a GhRoomLabel. A room's rules are OR'd — an item
// satisfies the room if it matches ANY one rule — so a single room can sort
// for multiple categories at once (e.g. "Chain Scale" = Chainmail OR
// Scalemail; "Necks Bracers" = worn at Neck OR worn at Arms).
//
// Two mutually exclusive shapes:
//   - Worn set, ItemType/WeaponType/ArmourType null: matches ANY item worn at
//     that equip slot regardless of its ItemType (jewelry-style rooms like
//     Neck / Wrist / Off-Hand aren't classified by material or weapon class).
//   - ItemType set, Worn null: the ordinary top-level-category rule.
//     WeaponType narrows it when ItemType is Weapon; ArmourType narrows it
//     when ItemType is Armour. Neither narrower field set = any item of that
//     ItemType (e.g. any Food, any Scroll).
public sealed class GhCategoryRule
{
    public int? Worn { get; set; }

    public int? ItemType { get; set; }
    public int? WeaponType { get; set; }
    public int? ArmourType { get; set; }

    public GhCategoryRule() { }

    public static GhCategoryRule ForWornSlot(int worn) => new() { Worn = worn };

    public static GhCategoryRule ForItemType(int itemType, int? weaponType = null, int? armourType = null) =>
        new() { ItemType = itemType, WeaponType = weaponType, ArmourType = armourType };
}
