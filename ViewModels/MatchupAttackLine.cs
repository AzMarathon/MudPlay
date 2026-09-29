namespace MudPlay.ViewModels;

// One physical attack's line in Monster Intel's Your Matchup. IsRoundsBasis marks
// the attack picked (Edit Attacks radio) to drive Est. Rounds to Kill, which the
// panel highlights so the attack you're judging monsters by stands out. Detail is
// an optional second line of the working behind the verdict (the backstab uses it).
public sealed record MatchupAttackLine(string Text, string? Detail, bool IsRoundsBasis)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}
