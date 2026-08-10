using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// ITravelCostModel for ParaMUD realms. Instead of a flat or bucketed
// seconds-per-hop, it evaluates the game's own movement-speed formula
// (MovementSpeedCalculator) against the player's live carry weight and
// worn-gear quickness, then adds a fixed per-hop latency. This tracks the
// player's real pace as they pick up loot (encumbrance climbs) or swap gear
// (quickness shifts) without the user hand-entering hop times.
//
// Why the latency addend: MovementSpeedCalculator returns the server-side
// move timer only (clamped to the 1-second cap). The scheduler and the
// walk-to ETA reason in wall-clock, which always includes a round-trip
// network + render delay before the next room paints. The stock encumbrance
// buckets are measured wall-clock (latency already baked in, so they add
// nothing); the formula is pure server math, so we add the observed gap here.
// A uniform per-hop shift barely moves the auto-lair ranking but visibly
// firms up the displayed arrival estimate.
//
// Reads the inventory snapshot through a provider delegate (same pattern as
// PlayerIllumination) so the estimate always reflects the latest `i` dump
// without this model subscribing to anything.
public sealed class ParadigmMovementCostModel : ITravelCostModel
{
    // Fixed wall-clock delay added on top of the formula's per-hop timer.
    // Derived from the stock band's min→median gap (~0.35 s); a single
    // constant is enough until a measured per-realm latency proves worth it.
    public const double DefaultLatencySeconds = 0.35;

    private readonly Func<InventorySnapshot> _snapshot;
    private readonly GameDataCache _cache;
    private readonly double _latencySeconds;

    public ParadigmMovementCostModel(
        Func<InventorySnapshot> snapshot,
        GameDataCache cache,
        double latencySeconds = DefaultLatencySeconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(cache);
        _snapshot = snapshot;
        _cache = cache;
        _latencySeconds = Math.Max(0.0, latencySeconds);
    }

    public TimeSpan EstimateTravel(int hopCount)
    {
        if (hopCount <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(hopCount * PerHopSeconds());
    }

    private double PerHopSeconds()
    {
        InventorySnapshot snap = _snapshot();
        int encPercent = snap.Encumbrance.Percentage;
        int quickness = CharacterCalculator
            .AggregateEquipmentStats(snap.EquippedItems, _cache)
            .Totals.PlusQuickness;

        // The formula can dip below the 1-second cap (spare quickness); the
        // game clamps there, so the effective timer is never faster than the
        // cap. Slowness effects aren't tracked live yet — pass 0.
        MovementSpeedResult res = MovementSpeedCalculator.Compute(encPercent, quickness, slowness: 0);
        double effectiveMillis = Math.Max(res.SpeedMillis, MovementSpeedCalculator.CapMillis);
        return effectiveMillis / 1000.0 + _latencySeconds;
    }
}
