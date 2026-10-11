namespace MudPlay.Game.Simulation;

// How a kept ranking stands against the character as it is now.
//   Unchecked  the character can't be read yet (no `stat`, no inventory): nothing
//              is known either way until it can.
//   NoRecord   the ranking carries no record of the character it was made for (it
//              was made before the inventory had been read): nothing can ever be
//              known, and it is taken as changed.
//   Changed    otherwise, the groups that are not what they were; none when it is
//              still the character the ranking was made for.
public readonly record struct RankingCheck(bool Unchecked, IReadOnlyList<string>? Changed, bool NoRecord = false)
{
    public static readonly RankingCheck NotChecked = new(true, null);

    public bool IsCurrent => !Unchecked && !NoRecord && Changed is not { Count: > 0 };

    public static RankingCheck Of(RankingCharacter? made, RankingCharacter? now) =>
        made is null ? new RankingCheck(false, null, NoRecord: true)
        : now is null ? NotChecked
        : new RankingCheck(false, now.ChangedSince(made));
}
