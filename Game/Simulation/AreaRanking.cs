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
public sealed record AreaRanking(
    IReadOnlyList<AreaRank> Rows, int Level, double Step, double Hours, int Runs, RoomKey From, string Realm,
    string Summary, DateTimeOffset MadeAt, int CharacterLevel)
{
    // What leads the status line of a ranking listed from those kept, as against one
    // just made: when it was made and at what level, and the character's level now
    // when that is known (0 is not: no `stat` read yet) and is no longer what it was.
    public string KeptNote(int levelNow, DateTimeOffset now)
    {
        DateTimeOffset made = MadeAt.ToLocalTime();
        string when = made.Date == now.ToLocalTime().Date ? $"{made:HH:mm}" : $"{made:yyyy-MM-dd HH:mm}";
        string note = $"Made {when} at L{Level}";
        if (!IsCurrentAt(levelNow))
            note += CharacterLevel == Level
                ? $"; you are L{levelNow} now"
                : $"; you were L{CharacterLevel} then and are L{levelNow} now";
        return note + ".";
    }

    // False when the character is known to have changed level since this was made.
    public bool IsCurrentAt(int levelNow) => levelNow <= 0 || levelNow == CharacterLevel;
}
