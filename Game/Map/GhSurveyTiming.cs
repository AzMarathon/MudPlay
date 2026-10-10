namespace MudPlay.Game.Map;

// What one read of a gang-house room's floor cost a Roomba sweep, by stage: Read is
// splitting the "You notice" list into items, Merge is folding it into the sweep's
// record of the room. Stacks and Items size what the room held on record after it.
// Runtime only — never persisted.
public sealed record GhSurveyTiming(RoomKey Room, int Stacks, int Items, TimeSpan Read, TimeSpan Merge)
{
    public TimeSpan Total => Read + Merge;

    public string StagesText =>
        $"read {Read.TotalMilliseconds:F1} ms, merge {Merge.TotalMilliseconds:F1} ms";
}
