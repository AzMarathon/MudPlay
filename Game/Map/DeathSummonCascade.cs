using System;
using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Resolves a room's death-summon cascade for the exp estimator: seed the room with
// its base spawn, then walk it tier by tier — each monster that dies fires its
// DeathSpell summons, those become the next tier, and so on until a tier summons
// nothing.
//
// The engine caps a room at 20 living monsters, and summons that would push past
// that never spawn. Since a tier's monsters are all alive together (they spawned
// as one wave and die as one), the cap applies per tier: if a tier's summons total
// more than 20, only 20 spawn — the excess is suppressed and never fought or
// counted. That keeps a fan-out room (say 15 mobs each summoning 2 → 30) from being
// scored as if all 30 appeared. Suppression is proportional across the tier's types
// so its composition (and thus average exp) is preserved; single-type rooms — the
// common case, e.g. the Zombie Pen — are exact.
//
// The result is room-level: Exp over every monster actually fought, Kills (the
// single-target round count), and Waves (the tier count = AoE clear passes). A
// monster that doesn't summon yields (seedCount × exp, seedCount, 1) — the no-op the
// estimator falls back to for an ordinary lair.
public static class DeathSummonCascade
{
    // Engine hard limit on simultaneous monsters in a room. Summons beyond it are
    // suppressed. (Confirmed game mechanic.)
    public const int RoomMonsterCap = 20;

    // Cycle / runaway guard: real chains are ≤3 tiers deep and terminate on their
    // own (a tier whose members have no DeathSpell). The cap only fires on malformed
    // data that summons in a loop.
    public const int MaxTiers = 8;

    public static CascadeResult Simulate(
        int seedType, int seedCount,
        Func<int, int> expOf, Func<int, IReadOnlyList<int>?> summonsOf,
        int cap = RoomMonsterCap, int maxTiers = MaxTiers)
    {
        ArgumentNullException.ThrowIfNull(expOf);
        ArgumentNullException.ThrowIfNull(summonsOf);

        var current = new Dictionary<int, double> { [seedType] = Math.Min(Math.Max(0, seedCount), cap) };
        double totalExp = 0;
        double totalKills = 0;
        int waves = 0;

        while (current.Count > 0 && waves < maxTiers)
        {
            waves++;
            foreach ((int id, double cnt) in current)
            {
                totalExp += cnt * expOf(id);
                totalKills += cnt;
            }

            var next = new Dictionary<int, double>();
            double nextTotal = 0;
            foreach ((int id, double cnt) in current)
            {
                IReadOnlyList<int>? kids = summonsOf(id);
                if (kids is null) continue;
                foreach (int k in kids)
                {
                    next[k] = next.GetValueOrDefault(k) + cnt;
                    nextTotal += cnt;
                }
            }
            if (nextTotal <= 0) break;

            // Room cap: only 20 of the summoned wave can occupy the room. Scale the
            // whole tier proportionally so its type mix — and the exp it's worth —
            // is preserved while the head-count is clamped.
            if (nextTotal > cap)
            {
                double factor = cap / nextTotal;
                foreach (int k in new List<int>(next.Keys)) next[k] *= factor;
            }
            current = next;
        }

        return new CascadeResult(totalExp, totalKills, waves);
    }
}

// Room-level totals from a death-summon simulation. Exp is over every monster
// actually fought (post-cap); Kills is the single-target round count (post-cap);
// Waves is the number of summon tiers (AoE clear passes).
public readonly record struct CascadeResult(double Exp, double Kills, int Waves);
