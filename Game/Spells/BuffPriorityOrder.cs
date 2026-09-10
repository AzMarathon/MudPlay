using MudPlay.Models.Profile;

namespace MudPlay.Game.Spells;

// Pure buff-list ordering shared by the Buff Watchdog config display and the
// CastingDirector priority walk, so the "grouped display" and the "Default cast
// order" agree on one category definition. Category buckets: 0 = self /
// single-target, 1 = whole-party, 2 = item ("on use"). Whether the list is
// grouped-by-category or walked in the user's arranged order is decided by the
// two BuffSettings flags — see ManualOrder (layout) and PriorityTopDown (priority).
public static class BuffPriorityOrder
{
    public static int Category(bool isItem, bool isWholeParty) =>
        isItem ? 2 : isWholeParty ? 1 : 0;

    // The order the casting engine walks due buffs in. Only a hand-arranged list
    // asked to cast top-to-bottom is walked as-is; otherwise (and in Default
    // priority) it's grouped by Category. OrderBy is stable, so equal-category
    // slots keep the user's arranged relative order. categoryOf is supplied by the
    // caller (it needs a live spell-scope lookup the slot doesn't store).
    public static IReadOnlyList<BuffSlot> InPriorityOrder(
        IReadOnlyList<BuffSlot> slots, bool priorityTopDown, bool manualOrder,
        Func<BuffSlot, int> categoryOf)
    {
        if (priorityTopDown && manualOrder) return slots;
        return slots.OrderBy(categoryOf).ToList();
    }

    // The order the config table SHOWS the list in — the user's arrangement once
    // hand-arranged, otherwise grouped by category. (Distinct from InPriorityOrder,
    // which also weighs PriorityTopDown.) The read-only timer bars follow this so
    // they line up with the config rows.
    public static IReadOnlyList<BuffSlot> InDisplayOrder(
        IReadOnlyList<BuffSlot> slots, bool manualOrder, Func<BuffSlot, int> categoryOf)
        => manualOrder ? slots : slots.OrderBy(categoryOf).ToList();
}
