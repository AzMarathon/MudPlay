using System.Collections.Generic;

namespace MudPlay.Game.Calculators;

// One member's Paradigm aggro breakdown: the four score terms, the raw total, the
// floored score the lottery actually uses, and the member's share of the weighted
// lottery (0-100%).
public sealed record ParadigmAggroMemberResult(
    string Name,
    int Base,
    int CharmDelta,
    int PositionBonus,
    int AggroDelta,
    int RawScore,
    int Score,          // RawScore floored at ParadigmAggroCalculator.ScoreFloor
    double Percent);

// The whole party's Paradigm result: each member's line and the summed score the
// monster's weighted lottery rolls within.
public sealed record ParadigmAggroResult(
    IReadOnlyList<ParadigmAggroMemberResult> Members, int TotalScore);
