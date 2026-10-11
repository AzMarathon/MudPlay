namespace MudPlay.Game.Simulation;

// How a kept ranking stands against the character as it is now.
//   Unchecked     the character can't be read yet (no `stat`, no inventory on this
//                 connection): nothing is known of it either way until it can.
//   NoRecord      the ranking carries no record of the character it was made for
//                 (it was made before the character had been read): nothing can
//                 ever be known, and it is taken as changed.
//   Changed       otherwise, the groups that are not what they were; none when it
//                 is still the character the ranking was made for.
//   ChangedLoops  the user's own loops in the ranking that are not what they were
//                 (RankingLoops), by name. Known whatever the character's state:
//                 the loops are read from the catalogue, not from the game.
public readonly record struct RankingCheck(
    bool Unchecked, IReadOnlyList<string>? Changed, bool NoRecord = false, IReadOnlyList<string>? ChangedLoops = null)
{
    public static readonly RankingCheck NotChecked = new(true, null);

    public bool IsCurrent => !Unchecked && !NoRecord && Changed is not { Count: > 0 } && ChangedLoops is not { Count: > 0 };

    // Every group to name as changed, the loops last.
    public IReadOnlyList<string> Groups =>
        ChangedLoops is { Count: > 0 }
            ? (Changed ?? Array.Empty<string>()).Append(RankingLoops.Group).ToList()
            : Changed ?? Array.Empty<string>();

    public static RankingCheck Of(RankingCharacter? made, RankingCharacter? now, IReadOnlyList<string>? changedLoops = null) =>
        made is null ? new RankingCheck(false, null, NoRecord: true, ChangedLoops: changedLoops)
        : now is null ? NotChecked with { ChangedLoops = changedLoops }
        : new RankingCheck(false, now.ChangedSince(made), ChangedLoops: changedLoops);
}
