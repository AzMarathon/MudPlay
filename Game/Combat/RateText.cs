using System.Globalization;

namespace MudPlay.Game.Combat;

// Compact per-hour rate formatting shared by the Session Stats window and the
// main-window looping chip: large figures abbreviate to k / M so they fit a
// narrow chip without a comma-grouped run of digits (5749 -> "5.7k"). Invariant
// culture so the decimal point stays a dot on every locale.
public static class RateText
{
    // e.g. 5749 -> "5.7k", 1_200_000 -> "1.2M", 42 -> "42", <=0 -> "0".
    public static string Compact(double value)
    {
        if (value <= 0) return "0";
        return value switch
        {
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000d:0.#}M"),
            >= 1_000     => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000d:0.#}k"),
            _            => value.ToString("F0", CultureInfo.InvariantCulture),
        };
    }

    // A rate as a percent to a tenth — never "0%" or "100%" unless it is exactly
    // that, so a miss in 250 swings doesn't read as a perfect 100% hit rate (user,
    // 2026-09-30; report paradigm-20260930-213857 found it on Sneak %).
    public static string Percent(double percent)
    {
        if (double.IsNaN(percent)) return "—";
        double shown = Math.Round(percent, 1, MidpointRounding.AwayFromZero);
        if (shown >= 100 && percent < 100) shown = 99.9;
        if (shown <= 0 && percent > 0) shown = 0.1;
        return shown.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }
}
