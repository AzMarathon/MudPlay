namespace MudPlay.Game.GameData;

// One "Room(<command>) <rooms>" entry of an item's "Obtained From" cell: typing any
// one of Commands in any one of Rooms can give the item. Written by Nightmare Redux
// for Linux; an export without the entries simply has none.
//
// ChancePercent is the chance per use, null when the entry carries none or "(?%)".
// ChanceUnknown is "(?%)": a random give whose odds the exporter couldn't work out.
// An entry with neither is a direct give — no roll — which says nothing about what
// else the command asks for (an item to hand in, a level, a price); that stays in the
// room's command textblock.
public sealed record RoomCommandSource(
    IReadOnlyList<string> Commands,
    IReadOnlyList<(int Map, int Room)> Rooms,
    double? ChancePercent,
    bool ChanceUnknown)
{
    public bool IsDirect => ChancePercent is null && !ChanceUnknown;
}
