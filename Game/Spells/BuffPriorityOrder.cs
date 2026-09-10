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

    // STOCK collision-free ordering: on stock a buff's RemovesSpell fires only at cast,
    // so a one-directional loser X can coexist with its remover Y if Y is cast first (and
    // re-applied after each Y recast — which the clobber-clear + next-pass pick handles).
    // This stably re-sorts an already-priority-ordered list so every remover Y precedes
    // the losers X it removes, WITHOUT blocking: the picker still walks in order and falls
    // through to X if Y can't cast this pass (unaffordable / not due), so X never starves
    // behind an inert remover. loserToRemover is loser cast-code → remover cast-code
    // (BuffConflictAnalyzer.OneDirectionalRemoverCodes); empty (Paradigm, or no one-way
    // pairs) returns the list unchanged. Mutual pairs are already excluded from the map,
    // so the constraint graph is acyclic; a cycle-guard falls back to input order anyway.
    public static IReadOnlyList<BuffSlot> OrderRemoversFirst(
        IReadOnlyList<BuffSlot> slots, IReadOnlyDictionary<string, string> loserToRemover)
    {
        if (loserToRemover.Count == 0) return slots;

        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (BuffSlot s in slots)
            if (!string.IsNullOrWhiteSpace(s.Spell)) present.Add(s.Spell!);

        // The remover a slot must wait for, or null when it isn't a loser (or its remover
        // isn't even configured — nothing to order against).
        string? RemoverOf(BuffSlot s) =>
            s.Spell is { } c && loserToRemover.TryGetValue(c, out string? r) && present.Contains(r)
                ? r : null;

        // Stable topological emit: repeatedly take the first remaining slot whose blocking
        // remover has already been emitted (or it has none), preserving input order otherwise.
        List<BuffSlot> remaining = new(slots);
        List<BuffSlot> result = new(slots.Count);
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            int idx = -1;
            for (int i = 0; i < remaining.Count; i++)
            {
                string? rem = RemoverOf(remaining[i]);
                if (rem is null || emitted.Contains(rem)) { idx = i; break; }
            }
            if (idx < 0) idx = 0;   // cycle guard (shouldn't happen — mutual pairs excluded)
            BuffSlot slot = remaining[idx];
            remaining.RemoveAt(idx);
            result.Add(slot);
            if (!string.IsNullOrWhiteSpace(slot.Spell)) emitted.Add(slot.Spell!);
        }
        return result;
    }
}
