namespace MudPlay.Game.Inventory;

// One worn item parsed from an 'i' dump: the game prints equipped items inline
// with a trailing (<Slot>) suffix (e.g. "padded vest (Torso)"), while
// carried-but-unworn items have no suffix. InventoryManager harvests these so
// the Character Workshop can aggregate equipment bonuses against game data.
//
// Name is the bare item name, slot suffix stripped. Slot is the normalized slot
// label (one of the 21-slot model values plus Weapon Hand / Off-Hand). The game
// writes a two-handed weapon as (Two handed) in your own inventory but
// (Weapon Hand) in player listings; both normalize to Weapon Hand here.
public readonly record struct EquippedItem(string Name, string Slot)
{
    public const string WeaponHand = "Weapon Hand";

    // The worn set with weapon in the weapon hand in place of whatever's there — the
    // Backstab set's weapon for the opener. Unchanged (same list) when weapon is null
    // or already in hand.
    public static IReadOnlyList<EquippedItem> WithWeapon(IReadOnlyList<EquippedItem> worn, string? weapon)
    {
        string? inHand = worn.FirstOrDefault(w => w.Slot == WeaponHand).Name;
        if (weapon is null || string.Equals(weapon, inHand, StringComparison.OrdinalIgnoreCase)) return worn;
        List<EquippedItem> swapped = worn.Where(w => w.Slot != WeaponHand).ToList();
        swapped.Add(new EquippedItem(weapon, WeaponHand));
        return swapped;
    }
}
