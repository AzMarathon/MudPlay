namespace MudPlay.Game.Map;

// The time limits ReconnectRestart works to, shared by every run kind.
public static class ReconnectRestartLimits
{
    // The reform reads the first room display, which a lit room prints within the
    // second. A room too dark to display never gives it one, and the run mustn't wait
    // on that for good.
    public static TimeSpan ReformHoldMax { get; } = TimeSpan.FromSeconds(5);

    // The longest a start waits on a room the tracker can't place, prompts or not:
    // an idle character at full HP may be sent no further prompt (user, 2026-10-17:
    // don't wait forever). Long enough for an `rm` or `sys st` and its retry.
    public static TimeSpan RoomWaitMax { get; } = TimeSpan.FromSeconds(12);
}
