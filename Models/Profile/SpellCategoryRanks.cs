namespace MudPlay.Models.Profile;

// Where the Priority buffs category goes in a spell-type priority list saved before
// it existed. Ranks are handled as nine numbers in a fixed order: Emergency heal,
// Minor party heal, Major party heal, Downed-ally heal, Minor self heal, Major self
// heal, Curing, Buffing, Debuffing.
//
// A list nobody reordered takes the current default order, Priority buffs between
// the two self heals. An edited list keeps its order and gets Priority buffs at the
// end (user, 2026-10-06).
public static class SpellCategoryRanks
{
    // The default when all nine were reorderable.
    private static readonly int[] NineDefault = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    // The default before Emergency and Downed-ally heal could be moved: seven ranks
    // were stored and those two sat at their built-in 1 and 4.
    private static readonly int[] SevenDefault = { 1, 1, 2, 4, 3, 4, 5, 6, 7 };

    // The current default order, as it reads without Priority buffs. A list already
    // arranged this way moves to the default with Priority buffs in its place.
    private static readonly int[] CurrentOrderOfNine = { 1, 3, 2, 6, 5, 4, 7, 8, 9 };

    // The current default, in the same order, and Priority buffs' place in it.
    private static readonly int[] CurrentDefault = { 1, 3, 2, 7, 6, 4, 8, 9, 10 };
    private const int CurrentDefaultPriorityBuffs = 5;

    // The nine ranks to keep and the rank Priority buffs takes.
    public static (int[] Nine, int PriorityBuffs) AdoptPriorityBuffs(IReadOnlyList<int> nine)
    {
        ArgumentNullException.ThrowIfNull(nine);
        if (nine.Count != NineDefault.Length)
            throw new ArgumentException("nine ranks expected", nameof(nine));

        bool unedited = nine.SequenceEqual(NineDefault) || nine.SequenceEqual(SevenDefault)
            || nine.SequenceEqual(CurrentOrderOfNine);
        return unedited
            ? ((int[])CurrentDefault.Clone(), CurrentDefaultPriorityBuffs)
            : (nine.ToArray(), nine.Max() + 1);
    }
}
