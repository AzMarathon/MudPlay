using System;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Health;

// Resolves a rest trigger + rest-max for a pool. The percentages read against the
// DEFAULT gear set's max (the loadout the user's rest %s are tuned for, so a Pre-rest
// set that swaps a +MaxHP/+MaxMana item doesn't move the target), then BOTH are
// capped at the CURRENT gear's real max so a rest set that lowers the pool can never
// push a target out of reach and strand the rest forever (report
// paradigm-20260902-052036). Falls back to the real max, then the live ratcheted max,
// when the default-set / stat-screen values aren't known yet. Absolute-mode thresholds
// pass through PoolThreshold.Resolve unchanged; only the real-max cap still applies.
internal static class RestThresholds
{
    public static (int Trigger, int Max) Resolve(
        ThresholdMode mode, int triggerPct, int maxPct,
        int defaultMax, int realMax, int liveMax)
    {
        int basis = defaultMax > 0 ? defaultMax : realMax > 0 ? realMax : liveMax;
        int trigger = PoolThreshold.Resolve(mode, triggerPct, basis);
        int max = PoolThreshold.Resolve(mode, maxPct, basis);
        if (realMax > 0)
        {
            trigger = Math.Min(trigger, realMax);
            max = Math.Min(max, realMax);
        }
        return (trigger, max);
    }

    // A single threshold (a flee / hang / heal trigger) resolved against the same
    // Default-set basis + real-max cap. Heal/run/hang anchor to the Default set's pool
    // like rest does, so a Pre-rest set that alters the pool doesn't shift them.
    public static int ResolveValue(
        ThresholdMode mode, int pct, int defaultMax, int realMax, int liveMax)
        => Resolve(mode, pct, pct, defaultMax, realMax, liveMax).Max;
}
