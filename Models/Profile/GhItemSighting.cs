using System;

namespace MudPlay.Models.Profile;

// One item's most-recently-observed gang-house room, for @roomba's replies.
// ItemName is the canonical (game-data) display name — the same normalized
// form GhSurveyMerger records into GhSweepManager's room observations — so a
// query matches regardless of how the item was worded on the floor.
public sealed class GhItemSighting
{
    public string ItemName { get; set; } = string.Empty;
    public int Map { get; set; }
    public int Room { get; set; }
    public DateTimeOffset SeenAt { get; set; }
}
