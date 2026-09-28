using MudPlay.Game.Calculators;

namespace MudPlay.Game.Spells;

// What the Stock reroll path needs to read a landed mana-regen roll back off the
// mana it pays, with no `abil 145` to ask: the tick inputs (level, stats, magery,
// gear +ManaRgn%), the roll spell's level-scaled range, the meditate tick (always
// the unscaled base — the engine never applies +ManaRgn% to it), whether mana is
// below max (a full pool shows no ticks), and whether the worn gear has settled (a
// tick mid gear-set swap can't be matched to one set's +ManaRgn% and stats). See
// GAME_MECHANICS "Mana regeneration & the ManaRgn breakpoints".
public readonly record struct StockManaRollContext(
    ManaRegenBreakpointCalculator.Inputs Inputs,
    int RollMin,
    int RollMax,
    int MeditateTick,
    bool ManaBelowMax,
    bool GearSettled = true);
