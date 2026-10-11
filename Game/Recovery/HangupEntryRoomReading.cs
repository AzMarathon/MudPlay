using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// What the room a character came back in says of a death by the hang-up
// (HangupDeathArrivalRooms.Read).
//   Says     — true: that is where the dead of the room it hung up in arrive;
//              false: a death by this hang-up would not have left it there;
//              null: nothing.
//   TakenFor — set when Says is true for a room that is not itself the arrival
//              room but one the map can't tell from it: the arrival room it
//              stands for, so the wording doesn't name the wrong room as the
//              place the dead arrive.
public readonly record struct HangupEntryRoomReading(bool? Says, RoomKey? TakenFor = null);
