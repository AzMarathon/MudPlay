namespace MudPlay.Services;

// What LearnedArrivalRoomStore.Learn made of a death seen in a room.
public enum LearnedArrivalOutcome
{
    // No set is active: nowhere to keep it.
    NotKept,

    // A new reading for a room with none, or with only the engine's other
    // default room.
    Learned,

    // The reading on file, seen again.
    Known,

    // It contradicts what was on file, and nothing has been seen twice: every
    // reading is kept, and none is used until one is seen again.
    Disputed,

    // A reading seen a second time while the room was disputed: it stands, and
    // the others are dropped.
    Settled,

    // It differs from a reading already seen at least twice, which stays in use.
    // This one is kept beside it with its own count.
    Challenged,

    // A challenger seen as often as the reading in use: it is the one in use
    // now, and the one it replaced is kept beside it.
    TookOver,
}
