namespace MudPlay.Models.Profile;

// One of the cast pass's buff timers on the character, as SavedBuffTimers keeps it.
public sealed class SavedBuffTimer
{
    // The buff's cast code, or the item-cast token of a buff an item gives.
    public string Short { get; set; } = string.Empty;

    // What it had left when the timers stopped. Below zero for one that had already
    // run out and was being kept for the wait a negative recast margin asks for.
    public int RemainingSec { get; set; }

    // The slot's recast margin and the buff's full length, as the timer carried them.
    public int MarginSec { get; set; }
    public int TotalSec { get; set; }

    // The card a draw item dealt, null for anything else.
    public string? Outcome { get; set; }
}
