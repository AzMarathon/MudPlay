using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels;

// One attack spell's row in Monster Intel's Your Matchup, flagged when it's the
// spell picked to drive Est. Rounds to Kill so the panel can highlight it.
public sealed record MatchupSpellRow(SpellEffectivenessResult Spell, bool IsRoundsBasis);
