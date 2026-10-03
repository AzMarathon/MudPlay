using MudPlay.Game.Calculators;

namespace MudPlay.Game.Map;

// Floor-1 timer feasibility gate for the Great Pyramid climb (see
// GAME_MECHANICS.md). Floor 1 must finish ~126 moves + 6 actions within 5 min of
// the first firepit `up` or the party scatters — so before committing to the
// climb we estimate whether the leader can make it and refuse up front if not.
//
// Stock: a Heavy (>66%) leader is a guaranteed timeout (movement penalty), so
// refuse on the encumbrance band alone. Paradigm: run the game's own movement
// formula (MovementSpeedCalculator) against live carry weight + worn quickness
// and compare 126·per-move + 6·250 ms against the 5-min cap. Pure math — the
// caller supplies realm + live encumbrance / quickness.
public readonly record struct PyramidPreflightResult(bool Feasible, string Reason);

public static class PyramidPreflight
{
    // A small wall-clock margin held back from the 5-min cap so an estimate that
    // lands right at the edge is refused rather than sent to a coin-flip.
    private static readonly System.TimeSpan Margin = System.TimeSpan.FromSeconds(20);

    // Lag rides on top of the game's own per-move timer, so a real hop runs about a
    // tenth longer than the formula says (see GAME_MECHANICS "Per-hop movement
    // speed"). The solver waits for each arrival rather than pacing on a clock, so
    // this only sizes estimates: the F1 timer check below and how long a move may
    // go unanswered before it counts as stalled.
    private const double LagBufferFactor = 1.10;

    // The expected Paradigm time per move: the game's own movement formula (never
    // below the 1-second cap) plus the lag allowance. Paradigm only — stock movement
    // isn't formula-tracked, and this preflight gates stock on the encumbrance band.
    public static double PacedPerMoveMs(int encumbrancePercent, int quickness)
    {
        MovementSpeedResult res = MovementSpeedCalculator.Compute(encumbrancePercent, quickness, slowness: 0);
        return System.Math.Max(res.SpeedMillis, MovementSpeedCalculator.CapMillis) * LagBufferFactor;
    }

    public static PyramidPreflightResult Evaluate(
        bool isParadigm, int encumbrancePercent, EncumbranceLevel level, int quickness)
    {
        if (!isParadigm)
        {
            // Stock realms: the Heavy band alone dooms the timed floor.
            return level == EncumbranceLevel.Heavy
                ? new PyramidPreflightResult(false,
                    "leader is Heavy — floor-1 timer will scatter the party before it clears")
                : new PyramidPreflightResult(true, "encumbrance within floor-1 timer budget");
        }

        // Paradigm: the climb moves as fast as the game lets it, so the estimate is
        // the expected time per move (see PacedPerMoveMs) over the floor.
        double perMoveMs = PacedPerMoveMs(encumbrancePercent, quickness);
        double estimateMs = PyramidScript.Floor1MoveCount * perMoveMs
                          + PyramidScript.Floor1ActionCount * PyramidScript.ActionMillis;
        double budgetMs = PyramidScript.Floor1Budget.TotalMilliseconds - Margin.TotalMilliseconds;

        if (estimateMs > budgetMs)
            return new PyramidPreflightResult(false,
                $"floor-1 estimate {estimateMs / 1000.0:0.0}s exceeds the ~5-min timer "
                + $"(carry {encumbrancePercent}% at {perMoveMs / 1000.0:0.00}s/room) — too slow, would scatter");

        return new PyramidPreflightResult(true,
            $"floor-1 estimate {estimateMs / 1000.0:0.0}s within the timer");
    }
}
