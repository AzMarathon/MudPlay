namespace MudPlay.Game.Map;

// One line of the "teleports automatic walks may use" list: every teleport from one
// area into another, taken together. Area is where they land; RoomsBeyond is how
// many rooms there can be reached no other way, which is what tells a hub from a
// cupboard; From and Commands say where they are and what is typed.
public sealed record TeleportChoice(
    string Area,
    int RoomsBeyond,
    string From,
    string Commands,
    IReadOnlyList<(RoomKey From, RoomKey To)> Exits);
