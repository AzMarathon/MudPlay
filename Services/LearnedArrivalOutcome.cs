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

    // It contradicts a reading already seen at least twice. That one stands, and
    // this one is dropped.
    Overruled,
}
