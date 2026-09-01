using System;
using System.Collections.Generic;

namespace MudPlay.Game.Calculators;

// Stock MajorMUD monster-targeting model (realm Stock), reconstructed from the DLL
// + GAME_MECHANICS.md. Three stages:
//   1. Acquisition — who the monster opens on, by its Align (+ guard) vs the
//      player's alignment title (CONFIRMED, GAME_MECHANICS "Monster aggression").
//   2. Spread pick — among aggroed members in room order, each rolls to be the
//      target with chance 50 − 5×(their incoming hits); first pass wins, and the
//      leftover "nobody passed" mass falls to the last aggroed member (the DLL's
//      last-eligible fallback).
//   3. Stickiness — Follow% is the monster's "attack last" dial: how likely it is
//      to lock onto whoever it just hit rather than re-spread next beat.
// Pure math. Paradigm uses a completely different weighted-lottery model — see
// ParadigmAggroCalculator; the two never share a formula.
public static class StockAggroCalculator
{
    private static readonly int SeedyValue = AlignmentBands.ValueOf("Seedy") ?? 40;    // evil bucket floor
    private static readonly int OutlawValue = AlignmentBands.ValueOf("Outlaw") ?? 80;   // guard-aggro floor

    // align         — Monsters-table Align: 0 Good, 1 Evil, 2 Chaotic Evil,
    //                 3 Neutral, 4 Lawful Good, 5 Neutral Evil, 6 Lawful Evil.
    // isGuard        — a law-enforcing guard (aggros Outlaw-or-worse titles).
    // followPercent  — the monster's Follow% (0-100).
    // members        — party members in room / terminal order.
    public static StockAggroResult Compute(
        int align, bool isGuard, int followPercent, IReadOnlyList<StockAggroMember> members)
    {
        var rows = new List<StockAggroMemberResult>();
        if (members is null || members.Count == 0)
            return new StockAggroResult(rows, 0, Stickiness(align, followPercent));

        // Stage 1 — acquisition.
        int n = members.Count;
        var aggroed = new bool[n];
        var reason = new string[n];
        for (int i = 0; i < n; i++)
            (aggroed[i], reason[i]) = Acquire(align, isGuard, members[i]);

        // Stage 2 — spread pick among the aggroed members, in order.
        //   p_i    = clamp(50 − 5×hits_i, 0, 100)/100     chance to pass its own roll
        //   pick_i = Π_{earlier aggroed j}(1 − p_j) × p_i
        //   leftover Π(1 − p_j) (nobody passed) → last aggroed member (fallback).
        double carry = 1.0;
        int lastAggroed = -1;
        var pick = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!aggroed[i]) continue;
            double p = Math.Clamp(50 - 5 * members[i].IncomingHits, 0, 100) / 100.0;
            pick[i] = carry * p;
            carry *= 1 - p;
            lastAggroed = i;
        }
        if (lastAggroed >= 0) pick[lastAggroed] += carry;

        int aggroedCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (aggroed[i]) aggroedCount++;
            rows.Add(new StockAggroMemberResult(
                members[i].Name, aggroed[i], reason[i], aggroed[i] ? 100.0 * pick[i] : 0.0));
        }
        return new StockAggroResult(rows, aggroedCount, Stickiness(align, followPercent));
    }

    // Stage 1 rules (GAME_MECHANICS.md "Monster aggression", CONFIRMED):
    //   provoked                → always aggroed (you hit it first).
    //   guard vs Outlaw+ title  → aggroed.
    //   Align 1/2/5             → opens on everyone.
    //   Align 6 (Lawful Evil)   → opens on non-evil (Neutral-or-better), spares the
    //                             evil bucket (Seedy and worse).
    //   Align 0/3/4             → passive by alignment (won't open unprovoked).
    private static (bool Aggroed, string Reason) Acquire(int align, bool isGuard, StockAggroMember m)
    {
        if (m.HasProvoked) return (true, "provoked (hit it first)");

        int v = AlignmentBands.ValueOf(m.AlignmentTitle) ?? 0;   // unknown title → Neutral

        if (isGuard && v >= OutlawValue) return (true, "guard vs Outlaw-or-worse");

        switch (align)
        {
            case 1 or 2 or 5:
                return (true, "evil mob — opens on all");
            case 6:
                return v >= SeedyValue
                    ? (false, "lawful evil spares the evil-titled")
                    : (true, "lawful evil opens on non-evil");
            default:   // 0 Good, 3 Neutral, 4 Lawful Good
                return (false, "passive align — won't open unprovoked");
        }
    }

    // Stage 3 — the Follow% stickiness readout.
    private static string Stickiness(int align, int followPercent)
    {
        int fp = Math.Clamp(followPercent, 0, 100);
        bool aggressive = align is not (0 or 3 or 4);
        if (!aggressive)
            return $"Follow {fp}% — passive align: once locked it keeps hitting that target and never re-spreads.";
        if (fp >= 100)
            return "Follow 100% — locks onto whoever it just hit and never lets go.";
        if (fp <= 0)
            return "Follow 0% — never locks; re-spreads across the party every beat.";
        double avgBeats = 100.0 / (100 - fp);
        return $"Follow {fp}% — aggressive: after each hit it re-locks {fp}% of the time, else re-spreads " +
               $"(~{avgBeats:0.0} beats on one target before it drifts).";
    }
}
