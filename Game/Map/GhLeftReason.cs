namespace MudPlay.Game.Map;

// Why a Roomba sweep left a floor item where it was, for surfacing in GhSweepReport
// / the GH Management tab so "left in place" isn't an undifferentiated bucket.
public enum GhLeftReason
{
    // No labeled room matched the item and there was no catch-all room.
    NoMatchingRoom,
    // A `get` for it failed because it was gone by sort time (decayed / taken).
    GoneBySortTime,
    // Too heavy to ever carry within the working encumbrance budget, so no
    // delivery could free enough room to move it.
    TooHeavy,
    // Every room that could hold it — each labeled match, then the catch-all —
    // refused the drop as full. Requeueing it would just retry the same wall, so
    // it waits for the next sweep, by which time the rooms may have space.
    AllDestinationsFull,
    // A drop was refused with the currency-syntax misparse and a fresh `i` showed
    // the item isn't held at all — a pickup the ledger recorded that never landed.
    // The move is a phantom, so it's removed rather than retried.
    NotActuallyCarried,
    // Auto-discard is configured to throw this item away, so sorting it would be
    // a tug of war: collect, get binned, find it again next lap. Left alone.
    AutoDiscarded,
}
