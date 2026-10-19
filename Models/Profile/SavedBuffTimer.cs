using MudPlay.Services;

namespace MudPlay.Models.Profile;

// One of the cast pass's buff timers on the character, as SavedBuffTimers keeps it.
public sealed class SavedBuffTimer
{
    // The buff's cast code, or the item-cast token of a buff an item gives.
    public string Short { get; set; } = string.Empty;

    // What it had left when the timers stopped. Below zero for one that had already
    // run out and was being kept for the wait a negative recast margin asks for.
    [SettingRange(-1_000_000, 1_000_000)]
    public int RemainingSec { get; set; }

    // The slot's recast margin and the buff's full length, as the timer carried them.
    [SettingRange(-999, 999)]
    public int MarginSec { get; set; }
    [SettingRange(0, 1_000_000)]
    public int TotalSec { get; set; }

    // The card a draw item dealt, null for anything else.
    public string? Outcome { get; set; }

    // When the cast runs out by the clock that never stopped: the expiry it would
    // have had with no time out of the game at all, this stretch and any before it.
    // That is where a whole-party buff ends on the members it covered, whose buffs
    // ran on while ours stood still. A record without it reads as long run out,
    // which errs on the side of casting again.
    public DateTime RanOnUntilUtc { get; set; }
}
