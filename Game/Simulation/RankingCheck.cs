namespace MudPlay.Game.Simulation;

// How a kept ranking stands against the character as it is now. Unchecked: the
// character can't be read yet (no `stat`, no inventory) or the ranking carries no
// record of it, so nothing is known either way. Otherwise Changed names the groups
// that are not what they were, none when it is still the character it was made for.
public readonly record struct RankingCheck(bool Unchecked, IReadOnlyList<string>? Changed)
{
    public static readonly RankingCheck NotChecked = new(true, null);

    public bool IsCurrent => !Unchecked && Changed is not { Count: > 0 };

    public static RankingCheck Of(RankingCharacter? made, RankingCharacter? now) =>
        made is null || now is null ? NotChecked : new RankingCheck(false, now.ChangedSince(made));
}
