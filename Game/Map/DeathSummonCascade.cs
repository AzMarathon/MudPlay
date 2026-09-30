using System;
using System.Collections.Generic;

namespace MudPlay.Game.Map;

// Resolves a room's death-summon cascade for the exp estimator: seed the room with
// its base spawn, then walk it tier by tier — each monster that dies fires its
// DeathSpell summons, those become the next tier, and so on until a tier summons
// nothing.
//
// The engine caps a room's living monsters (GAME_MECHANICS "Death-summon
// cascades"). On Paradigm the cap is 20 and a monster's DeathSpell fires as a SINGLE
// atomic cast: the whole cast lands only if all its summoned monsters fit — otherwise
// it fails outright and is permanently lost (no partial spawn, no retry). So a tier
// fills with whole casts until the next one won't fit; the rest are dropped. On Stock
// the room holds 15 and each summon is placed on its own, as many as fit. Either way
// a fan-out room (say 15 mobs each summoning 2 → 30) isn't scored as if all 30
// appeared. Casts are processed in a deterministic order (ascending monster
// id) since the engine's real death order isn't knowable; the difference only shows
// in a room that actually hits the cap, which is rare. Single-type rooms — the
// common case, e.g. the Zombie Pen — never hit it and are exact.
//
// The result is room-level: Exp over every monster actually fought, Kills (the
// single-target round count), and Waves (the tier count = AoE clear passes). A
// monster that doesn't summon yields (seedCount × exp, seedCount, 1) — the no-op the
// estimator falls back to for an ordinary lair.
public static class DeathSummonCascade
{
    // Engine limit on simultaneous monsters in a room: Paradigm 20, Stock 15 (the
    // room record's 15 monster slots).
    public const int RoomMonsterCap = 20;
    public const int StockRoomMonsterCap = 15;

    // The realm's room cap, and whether a death cast lands whole or not at all
    // (Paradigm) or summon by summon (Stock).
    public static (int Cap, bool WholeCasts) RulesFor(RealmType realm) =>
        realm == RealmType.ParaMud ? (RoomMonsterCap, true) : (StockRoomMonsterCap, false);

    // Cycle / runaway guard: real chains are ≤3 tiers deep and terminate on their
    // own (a tier whose members have no DeathSpell). The cap only fires on malformed
    // data that summons in a loop.
    public const int MaxTiers = 8;

    public static CascadeResult Simulate(
        int seedType, int seedCount,
        Func<int, int> expOf, Func<int, IReadOnlyList<int>?> summonsOf,
        int cap = RoomMonsterCap, int maxTiers = MaxTiers, bool wholeCasts = true)
    {
        ArgumentNullException.ThrowIfNull(expOf);
        ArgumentNullException.ThrowIfNull(summonsOf);

        // SortedDictionary → deterministic cast order (ascending monster id) when a
        // tier holds more than one summoner type and the cap forces a choice.
        var current = new SortedDictionary<int, int> { [seedType] = Math.Min(Math.Max(0, seedCount), cap) };
        double totalExp = 0;
        double totalKills = 0;
        int waves = 0;

        while (current.Count > 0 && waves < maxTiers)
        {
            waves++;
            foreach ((int id, int cnt) in current)
            {
                totalExp += (double)cnt * expOf(id);
                totalKills += cnt;
            }

            var next = new SortedDictionary<int, int>();
            int nextCount = 0;
            foreach ((int id, int cnt) in current)
            {
                IReadOnlyList<int>? kids = summonsOf(id);
                if (kids is null || kids.Count == 0) continue;
                int castSize = kids.Count;

                if (!wholeCasts)
                {
                    // Summon by summon: each of the `cnt` casts places what still fits.
                    for (int c = 0; c < cnt && nextCount < cap; c++)
                        foreach (int k in kids)
                        {
                            if (nextCount >= cap) break;
                            next[k] = next.GetValueOrDefault(k) + 1;
                            nextCount++;
                        }
                    continue;
                }
                // Whole casts only: how many of this type's `cnt` summon casts fit in
                // the room's remaining slots. A cast that doesn't fit is lost, not
                // trimmed — and further same-size casts can't fit either.
                int fits = Math.Min(cnt, (cap - nextCount) / castSize);
                if (fits <= 0) continue;
                foreach (int k in kids) next[k] = next.GetValueOrDefault(k) + fits;
                nextCount += fits * castSize;
            }
            if (nextCount == 0) break;
            current = next;
        }

        return new CascadeResult(totalExp, totalKills, waves);
    }
}

// Room-level totals from a death-summon simulation. Exp is over every monster
// actually fought (post-cap); Kills is the single-target round count (post-cap);
// Waves is the number of summon tiers (AoE clear passes).
public readonly record struct CascadeResult(double Exp, double Kills, int Waves);
