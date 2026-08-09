using System.Collections.Generic;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One step in the breakpoint strip: the roll value at which the spell's mana tick
// steps up, and the tick (MP) reached from there.
public readonly record struct ManaBreakpointMark(int Roll, int Tick);

// The data the breakpoint strip renders — a compact step chart of mana tick (Y)
// against the spell's roll value (X). The tick holds WorstTick from RollMin up to
// the first mark, then steps up at each mark to BestTick. RecommendedRoll is the
// suggested reroll threshold (null when no step is reachable). Replaces the old
// variable-length breakpoint table.
public sealed record ManaBreakpointStripData(
    int RollMin, int RollMax, int WorstTick, int BestTick,
    int? RecommendedRoll, IReadOnlyList<ManaBreakpointMark> Marks);
