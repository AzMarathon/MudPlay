namespace FujinTerm.Game.Combat;

// Immutable per-step HP/MA min–max profile for the Session Stats "HP/MA History"
// graph, published by HpMaHistoryTracker. Each array is indexed by loop step
// position (0..N-1 of the current circuit); the four arrays share that length, so
// step i's band is (HpLow[i]..HpHigh[i]) and (MaLow[i]..MaHigh[i]). Values are
// percent of max (0–100). HasMana is false for a no-mana class, so the panel can
// hide the mana band. Empty until the first on-loop sample lands.
public readonly record struct HpMaHistoryStats(
    IReadOnlyList<double> HpLow,
    IReadOnlyList<double> HpHigh,
    IReadOnlyList<double> MaLow,
    IReadOnlyList<double> MaHigh,
    bool HasMana)
{
    public static HpMaHistoryStats Empty { get; } = new(
        System.Array.Empty<double>(), System.Array.Empty<double>(),
        System.Array.Empty<double>(), System.Array.Empty<double>(), false);

    // Lowest HP% across every recorded step — the worst dip the loop produced,
    // for the panel's headline. 100 when nothing has been sampled yet.
    public double LowestHpPercent => Min(HpLow);

    // Lowest MA% across every recorded step. 100 when no mana sampled.
    public double LowestMaPercent => Min(MaLow);

    private static double Min(IReadOnlyList<double> s)
    {
        double lo = 100.0;
        bool any = false;
        foreach (double v in s) { if (!any || v < lo) lo = v; any = true; }
        return any ? lo : 100.0;
    }
}
