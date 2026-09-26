using MudPlay.Game.Map;

namespace MudPlay.Game.Remote;

// What another MudPlay user's @path reply says their movement engine is doing.
// LeaderRoom is where they stand; Destination is set for a walk-to, LoopName for a
// loop; Step / TotalSteps are the walker's 1-based progress ("step 94/166").
public sealed record PathReport(
    RoomKey LeaderRoom,
    RoomKey? Destination,
    string? LoopName,
    int Step,
    int TotalSteps)
{
    // Walker steps the leader still has to send, counting the one reported as current
    // (step 94/166 → 73). The walker reports the index of the NEXT step to send, so
    // everything from it onward is still ahead of them.
    public int StepsRemaining => Math.Max(0, TotalSteps - Step + 1);
}
