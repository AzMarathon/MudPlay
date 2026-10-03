namespace MudPlay.Game.Inventory;

// When the game refuses to drop or hide an item: "You may not drop that item!".
// Whatever can't be dropped can't be hidden either (user, 2026-10-02).
//   - The item's no-drop flag (game data "Not Droppable"), and a cursed item
//     (ability 82 Cursed or 83 CursedMajor) while it is worn: wccmmud.dll 1.11p
//     _cmd_drop and _cmd_hide make these two checks. A carried cursed item drops.
//   - A loyal item (ability 100), on either realm (user, 2026-10-02).
public static class ItemDropRule
{
    public const int CursedCode = 82;
    public const int CursedMajorCode = 83;
    public const int LoyalCode = 100;

    public static bool Refused(bool notDroppable, IEnumerable<int> abilities, bool worn)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        if (notDroppable) return true;
        foreach (int a in abilities)
        {
            if (a == LoyalCode) return true;
            if (worn && a is CursedCode or CursedMajorCode) return true;
        }
        return false;
    }
}
