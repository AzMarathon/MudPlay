namespace MudPlay.Game.Map;

// What one read of a gang-house room's floor cost a Roomba sweep, by stage: Read is
// splitting the "You notice" list into items, Merge is folding it into the sweep's
// record of the room, ItemLog is writing the room to the item-location log. Stacks
// and Items size the floor the read left on record. Runtime only — never persisted.
public sealed record GhSurveyTiming(
    RoomKey Room, int Stacks, int Items, TimeSpan Read, TimeSpan Merge, TimeSpan ItemLog)
{
    public TimeSpan Total => Read + Merge + ItemLog;

    public string StagesText =>
        $"read {Read.TotalMilliseconds:F1} ms, merge {Merge.TotalMilliseconds:F1} ms, "
        + $"item log {ItemLog.TotalMilliseconds:F1} ms";
}
