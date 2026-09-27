namespace MudPlay.Game.Train;

// The trainer's "not enough money" refusal gets exactly one funding recovery per
// run. The refusal means the client's purse figure was wrong, so the first one
// re-reads the purse and looks for funds. A second one after that means the money
// isn't there, and recovering again is the walk-to-the-trainer-and-back loop this
// gate exists to stop: the run must give up and back off instead.
//
// Kept apart from TrainerWalkManager (which needs a live map, walker and wire to
// construct) so the loop-breaking rule can be tested on its own.
public sealed class TrainRefusalGate
{
    private bool _used;

    // A new run gets its recovery back.
    public void Reset() => _used = false;

    // True when this refusal should re-read the purse and look for funds; false
    // when the run must give up and back off. canRecover is false when there's no
    // funding router, trainer or known position to recover with; that refusal gives
    // up without spending the run's one recovery.
    public bool TryClaimRecovery(bool canRecover)
    {
        if (_used || !canRecover) return false;
        _used = true;
        return true;
    }
}
