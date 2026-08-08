using System.Collections.Generic;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One plotted line in the Mana Regen chart: a stat value's label, its colour
// (ARGB), and the per-level tick amounts across the chart's level window (one
// entry per level from MinLevel to MaxLevel inclusive).
public sealed record ManaRegenChartSeries(string Label, uint ColorArgb, IReadOnlyList<int> Ticks);

// The data the Mana Regen chart control renders: the level window on the X axis,
// which level to mark as "current", and the stat-value series (natural tick vs
// level, so the step-ups are the breakpoints). Recomputed by the calculator VM.
public sealed record ManaRegenChartData(
    int MinLevel, int MaxLevel, int CurrentLevel, IReadOnlyList<ManaRegenChartSeries> Series);
