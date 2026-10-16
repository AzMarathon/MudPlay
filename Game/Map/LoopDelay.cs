namespace MudPlay.Game.Map;

// A loop room command's delay is stored in milliseconds (MegaMUD's .mp files carry
// it that way) and edited as minutes and seconds, up to MaxField in each box.
public static class LoopDelay
{
    public const long MaxField = 99999;

    public static long ToMs(long minutes, long seconds) =>
        (Math.Clamp(minutes, 0, MaxField) * 60 + Math.Clamp(seconds, 0, MaxField)) * 1000;

    // Whole minutes and the seconds past them; any part of a second (an imported
    // 500 ms) is dropped from the boxes but kept in the stored value until edited.
    // Past MaxField minutes the rest stays in the seconds box.
    public static (long Minutes, long Seconds) Split(long ms)
    {
        long totalSeconds = Math.Max(0, ms) / 1000;
        long minutes = Math.Min(totalSeconds / 60, MaxField);
        return (minutes, totalSeconds - minutes * 60);
    }

    public static string Describe(long ms)
    {
        if (ms < 1000) return $"{ms} ms";
        (long minutes, long seconds) = Split(ms);
        return minutes == 0 ? $"{seconds} s" : $"{minutes} min {seconds} s";
    }
}
