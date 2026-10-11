namespace MudPlay.Game.Recovery;

// How far HangupDeathArrivalRooms' list, read off the Stock map file, goes for the
// game data that is active.
public enum HangupArrivalListTrust
{
    // An unmodified Stock set: the list is that world's own, and says "that is
    // the wrong room" as well as "that is where they arrive".
    VanillaStock,

    // Paradigm. The rooms it shares with Stock send their dead where Stock does
    // (the owner, 2026-10-10), so for those the list is trusted as on Stock. Its
    // own rooms aren't on the list: one arrival room is known for them, and
    // nothing ever says "wrong room" there.
    Paradigm,

    // Anything else: a Stock board whose sysop may have edited its rooms, or no
    // set at all. The list can say "that is where they arrive" and "it never
    // moved", never "that is the wrong room".
    Unverified,
}
