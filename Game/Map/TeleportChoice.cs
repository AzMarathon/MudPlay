namespace MudPlay.Game.Map;

// One line of the "teleports automatic walks may use" list: one teleport spot. A
// teleport that also runs back the other way between the same two rooms is the
// same spot, so both directions are one line (TwoWay) and are allowed together.
// Area and RoomsBeyond say what the spot leads to: the area off the mainland at
// its far end, and how many rooms there can be reached no other way, which is what
// tells a hub from a cupboard. RoomsBeyond 0 is a shortcut between two places that
// can both be walked to.
public sealed record TeleportChoice(
    RoomKey From,
    string FromName,
    RoomKey To,
    string ToName,
    bool TwoWay,
    string Commands,
    string Area,
    int RoomsBeyond,
    IReadOnlyList<(RoomKey From, RoomKey To)> Exits)
{
    // The line as Settings → Teleports shows it, and as a refused automatic walk
    // names it so the user can find the box to tick. Map/room numbers beside both
    // names: a spot the user doesn't know by name can be looked up on the map.
    public string Title => TitleOf(From, FromName, To, ToName, TwoWay);

    public static string TitleOf(RoomKey from, string fromName, RoomKey to, string toName, bool twoWay) =>
        $"{fromName} ({from.Map}/{from.Room}) {(twoWay ? "⇄" : "→")} {toName} ({to.Map}/{to.Room})";
}
