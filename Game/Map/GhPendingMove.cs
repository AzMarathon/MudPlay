namespace MudPlay.Game.Map;

// One item queued to move from a mislabeled GH room to its correctly labeled one.
// Pure data — GhSweepManager wraps this in its own mutable dispatch tracking
// (its private PendingSortMove, with IsCarried / Delivered); this type only
// decides WHAT should move. Produced by GhSortQueueBuilder.
public sealed record GhPendingMove(RoomKey From, RoomKey To, string ItemName, int Count);
