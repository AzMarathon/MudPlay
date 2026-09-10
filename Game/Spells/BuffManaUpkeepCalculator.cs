using System.Collections.Generic;

namespace MudPlay.Game.Spells;

// How much mana per second it costs to keep a set of buff slots recast
// indefinitely — the Buff Watchdog's live "mana to maintain" readout. Pure
// arithmetic: the caller (BuffPanelViewModel) resolves each slot's spell,
// level-scaled mana cost / duration, and how many actual casts it fires per
// recast cycle right now (self, per targeted member, or one whole-party cast);
// this just turns that into a rate and sums it.
public static class BuffManaUpkeepCalculator
{
    // One slot's contribution: its spell's level-scaled mana cost and real
    // wall-clock duration (SpellCalculator.Duration × SpellRoundSecondsWallClock),
    // plus how many times it actually fires per cycle right now — 0 when the slot
    // isn't set to cast at all (e.g. every target box unticked), which excludes it
    // from the total rather than contributing a phantom cost.
    public readonly record struct SlotUpkeep(long ManaCost, double DurationSeconds, int CastsPerCycle)
    {
        // Mana spent per second maintaining this one slot. 0 when it never fires
        // or its duration is non-positive (shouldn't happen for a real buff post
        // BuffClassifier.HasDuration, but guards a division by zero rather than
        // propagating NaN/Infinity into the total).
        public double ManaPerSecond => CastsPerCycle <= 0 || DurationSeconds <= 0
            ? 0 : ManaCost * CastsPerCycle / DurationSeconds;
    }

    // Total mana/second across every slot's contribution.
    public static double TotalManaPerSecond(IEnumerable<SlotUpkeep> slots)
    {
        double total = 0;
        foreach (SlotUpkeep s in slots) total += s.ManaPerSecond;
        return total;
    }

    // The maintenance interval to budget a STOCK collision-order LOSER at — a
    // one-directional loser the Buff Watchdog keeps up by casting its remover first.
    // Every time the remover refreshes, its at-cast strip drops the loser and forces a
    // re-cast, so the loser's EFFECTIVE interval is the SHORTER of its own duration and
    // the remover's. When the remover's duration is shorter, the loser costs more mana
    // per tick than its own duration implies — cap the duration used in the budget so
    // "mana to maintain" stays honest. Returns ownSeconds when there's no remover
    // (removerSeconds <= 0) or the remover lasts at least as long (never forces an early
    // re-cast).
    public static double EffectiveMaintenanceSeconds(double ownSeconds, double removerSeconds)
        => removerSeconds > 0 && removerSeconds < ownSeconds ? removerSeconds : ownSeconds;
}
