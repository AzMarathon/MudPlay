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
}
