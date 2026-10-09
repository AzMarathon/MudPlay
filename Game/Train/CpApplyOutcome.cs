namespace MudPlay.Game.Train;

// What a pass over the `train stats` form came to, as far as it could be checked.
public enum CpApplyOutcome
{
    // The plan row's stats are trained. Only this lets the row be cleared.
    Applied,

    // CP was spent, but the row's stats weren't all reached. The row stays as the
    // goal for the next pass.
    Partial,

    // No CP left the character.
    NothingSpent,
}
