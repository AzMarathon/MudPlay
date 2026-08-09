namespace FujinTerm.Game.Inventory;

// Single source of truth for mapping a carry-weight percentage to its encumbrance
// band. Used both by the live parse (InventoryManager) and by what-if overlays
// (the Item Finder's trial gearset), so the two never drift. Stock boundaries:
// None ≤ 16%, Light ≤ 33%, Medium ≤ 66%, Heavy above. (Encumbered is a server-
// reported state, never derived locally.)
public static class EncumbranceCategory
{
    // Stock None/Light boundary (percent).
    public const int NoneCeiling = 16;

    public static EncumbranceLevel ForPercent(int percentage) => percentage switch
    {
        <= NoneCeiling => EncumbranceLevel.None,
        <= 33 => EncumbranceLevel.Light,
        <= 66 => EncumbranceLevel.Medium,
        _ => EncumbranceLevel.Heavy,
    };
}
