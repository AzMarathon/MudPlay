namespace MudPlay.Game.Inventory;

// When the game refuses to drop or hide an item: "You may not drop that item!".
// Read from wccmmud.dll 1.11p _cmd_drop and _cmd_hide, which make the same two
// checks: the item's no-drop flag (game data "Not Droppable"), and a cursed item
// (ability 82 Cursed or 83 CursedMajor) while it is worn. A carried cursed item
// drops. Loyal (ability 100) is not checked by either command.
public static class ItemDropRule
{
    public const int CursedCode = 82;
    public const int CursedMajorCode = 83;

    public static bool Refused(bool notDroppable, IEnumerable<int> abilities, bool worn)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        if (notDroppable) return true;
        return worn && abilities.Any(static a => a is CursedCode or CursedMajorCode);
    }
}
