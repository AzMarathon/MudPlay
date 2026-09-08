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
}
