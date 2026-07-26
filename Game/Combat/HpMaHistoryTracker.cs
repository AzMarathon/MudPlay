namespace FujinTerm.Game.Combat;

// Accumulates the min and max HP / mana (as percent of max) observed at each loop
// STEP POSITION, for the Session Stats "HP/MA History" graph. The graph answers
// "which steps of my loop are dangerous", so samples fold by step index
// (0..N-1 of the circuit) and repeat across laps into the same bucket — widening
// each step's band to the full range seen there over the whole run.
//
// Like the other session-stats trackers it owns no subscriptions: AppServices
// gates sampling on LoopRunner.State == Running, reads the live step index, and
// pushes (index, hp%, ma%) in via NoteVitals; a new loop (ReachedFirstWaypoint) or
// the connect / character-switch boundary clears it via Reset. Every call runs on
// the marshalled dispatch thread (the prompt scanner fires there), so the state is
// lock-free.
public sealed class HpMaHistoryTracker
{
    // One loop step's accumulated HP / mana band (percent of max). HasHp / HasMa
    // gate the first fold (which seeds low = high = the sample) vs. later folds
    // (which widen the band). Mana stays absent for a no-mana class.
    private readonly record struct StepBand(
        bool HasHp, double HpLow, double HpHigh,
        bool HasMa, double MaLow, double MaHigh)
    {
        public static StepBand Empty { get; } = new(false, 0, 0, false, 0, 0);

        public StepBand FoldHp(double pct) => HasHp
            ? this with { HpLow = Math.Min(HpLow, pct), HpHigh = Math.Max(HpHigh, pct) }
            : this with { HasHp = true, HpLow = pct, HpHigh = pct };

        public StepBand FoldMa(double pct) => HasMa
            ? this with { MaLow = Math.Min(MaLow, pct), MaHigh = Math.Max(MaHigh, pct) }
            : this with { HasMa = true, MaLow = pct, MaHigh = pct };
    }

    // Per-step bands, indexed by loop step position. Grows to cover the largest
    // index seen; cleared wholesale on Reset.
    private readonly List<StepBand> _steps = new();
    private bool _sawMana;

    // Raised after any input updates the profile, so the Session Stats VM can
    // refresh. Fires on the dispatch thread; the VM coalesces onto one tick.
    public event Action? Changed;

    // Fold one vitals sample into the given loop step's band. hpPercent is clamped
    // to 0–100; maPercent is null for a no-mana class (its band is never seeded).
    // Grows the per-step list so an out-of-order or later-in-the-circuit index is
    // covered.
    public void NoteVitals(int step, double hpPercent, double? maPercent)
    {
        if (step < 0) return;
        while (_steps.Count <= step) _steps.Add(StepBand.Empty);

        StepBand band = _steps[step].FoldHp(Clamp(hpPercent));
        if (maPercent is { } ma) { band = band.FoldMa(Clamp(ma)); _sawMana = true; }
        _steps[step] = band;
        Changed?.Invoke();
    }

    // Clear the whole per-step profile — a new loop re-anchors the circuit, and the
    // connect / character-switch boundary starts fresh. No-op (no Changed) when
    // already empty, so a redundant reset doesn't churn the VM.
    public void Reset()
    {
        if (_steps.Count == 0 && !_sawMana) return;
        _steps.Clear();
        _sawMana = false;
        Changed?.Invoke();
    }

    // Snapshot the four per-step series (percent of max), each indexed by step
    // position. A step that was never sampled (a step that produced no prompt)
    // carries the previous step's band forward so the plotted line stays
    // continuous across the rare gap; a leading gap uses the first sampled band.
    public HpMaHistoryStats Snapshot()
    {
        int n = _steps.Count;
        if (n == 0) return HpMaHistoryStats.Empty;

        StepBand first = StepBand.Empty;
        foreach (StepBand b in _steps) if (b.HasHp) { first = b; break; }
        if (!first.HasHp) return HpMaHistoryStats.Empty; // sized but no HP yet

        double[] hpLow = new double[n], hpHigh = new double[n];
        // Mana arrays stay empty for a no-mana class, so the panel's mana bars
        // vanish entirely rather than pinning to a phantom zero row.
        double[] maLow = _sawMana ? new double[n] : System.Array.Empty<double>();
        double[] maHigh = _sawMana ? new double[n] : System.Array.Empty<double>();
        StepBand carry = first;
        for (int i = 0; i < n; i++)
        {
            if (_steps[i].HasHp) carry = _steps[i];
            hpLow[i]  = carry.HpLow;
            hpHigh[i] = carry.HpHigh;
            if (_sawMana)
            {
                maLow[i]  = carry.HasMa ? carry.MaLow  : 0;
                maHigh[i] = carry.HasMa ? carry.MaHigh : 0;
            }
        }
        return new HpMaHistoryStats(hpLow, hpHigh, maLow, maHigh, _sawMana);
    }

    private static double Clamp(double pct) => pct < 0 ? 0 : pct > 100 ? 100 : pct;
}
