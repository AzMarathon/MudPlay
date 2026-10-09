using MudPlay.Game.Inventory;

namespace MudPlay.Game.Recovery;

// What a death leaves on the character instead of dropping it — GAME_MECHANICS
// "Death threshold & consequences": an item carrying LoyalItem (100) or CursedMajor
// (83) stays. A plain Cursed (82) item drops with the rest. Such an item is never on
// a floor to be found, so a recovery that waits for it never finishes.
public static class DeathPileRules
{
    public static bool StaysWithCharacter(IEnumerable<int> abilities)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        return abilities.Any(static a => a is ItemDropRule.LoyalCode or ItemDropRule.CursedMajorCode);
    }

    // A death record keeps item names, not numbers, and several items can share a
    // name. A name is left off a pile's missing list only when every item bearing it
    // stays: with one that drops among them, the pile would be closed with it still
    // lying on a floor. Takes the abilities of each item of that name.
    public static bool EveryItemOfTheNameStays(IEnumerable<IEnumerable<int>> itemsOfTheName)
    {
        ArgumentNullException.ThrowIfNull(itemsOfTheName);
        bool any = false;
        foreach (IEnumerable<int> abilities in itemsOfTheName)
        {
            if (!StaysWithCharacter(abilities)) return false;
            any = true;
        }
        return any;
    }
}
