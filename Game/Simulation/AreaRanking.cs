using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// One finished area ranking: the options best first, and what it ran with (the
// level, runs × hours, walk pace, the realm and the room it was ranked from), which
// a picked row's detail and a loop saved from it quote instead of settings changed
// since. Summary is the line shown above the list.
//
// MadeAt and CharacterLevel (the character's own level then, which Level is not
// when another level was ranked) are for a ranking listed again later: it says when
// it was made, and that the character has levelled since when it has.
//
// World and Character are what it was made in and for (RankingWorld,
// RankingCharacter): a ranking is kept between sessions, and these are how one read
// back from disk is known to be about this game data still, and whether about the
// character as it is now. FromDisk: it was read back, not made since the client
// started.
public sealed record AreaRanking(
    IReadOnlyList<AreaRank> Rows, int Level, double Step, double Hours, int Runs, RoomKey From, string Realm,
    string Summary, DateTimeOffset MadeAt, int CharacterLevel,
    RankingWorld? World = null, RankingCharacter? Character = null, bool FromDisk = false)
{
    // What leads the status line of a ranking listed from those kept, as against one
    // just made: when it was made and at what level; the character's level now when
    // that is known (0 is not: no `stat` read yet) and is no longer what it was; and
    // how it stands against the character otherwise (check), by the groups that have
    // changed or as not checked.
    public string KeptNote(int levelNow, DateTimeOffset now, RankingCheck check = default)
    {
        DateTimeOffset made = MadeAt.ToLocalTime();
        string when = made.Date == now.ToLocalTime().Date ? $"{made:HH:mm}" : $"{made:yyyy-MM-dd HH:mm}";
        string note = $"Made {when} at L{Level}";
        if (!IsCurrentAt(levelNow))
            note += CharacterLevel == Level
                ? $"; you are L{levelNow} now"
                : $"; you were L{CharacterLevel} then and are L{levelNow} now";
        if (check.Unchecked)
            note += "; not checked against the character yet";
        else if (check.NoRecord)
            note += "; the character may have changed since";
        else if (check.Changed is { Count: > 0 } changed)
            note += $"; your {Join(changed)} {(changed.Count == 1 && changed[0] is RankingCharacter.Reach ? "has" : "have")} changed since";
        return note + ".";
    }

    // False when the character is known to have changed since this was made, by
    // level or by what check found, or could not be checked: in doubt it is marked.
    public bool IsCurrentAt(int levelNow, RankingCheck check = default) =>
        (levelNow <= 0 || levelNow == CharacterLevel) && check.IsCurrent;

    // "a", "a and b", "a, b and c".
    private static string Join(IReadOnlyList<string> names) => names.Count == 1 ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
