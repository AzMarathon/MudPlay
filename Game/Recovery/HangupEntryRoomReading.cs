using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// What the room a character came back in says of a death by the hang-up
// (HangupDeathArrivalRooms.Read).
//   Says        — true: that is where the dead of the room it hung up in arrive;
//                 false: a death by this hang-up would not have left it there;
//                 null: nothing.
//   TakenFor    — set when Says is true for a room that is not itself the arrival
//                 room but one the map can't tell from it: the arrival room it
//                 stands for, so the wording doesn't name the wrong room as the
//                 place the dead arrive.
//   Albion      — Says is true because the room hung up in is in Paradigm's
//                 Albion (it can be walked to from the Burial Vault), not because
//                 a list names its arrival room.
//   LearnedFrom — Says is true because a death was once seen to end in that room
//                 (LearnedArrivalLookup), not because a list names it: the room
//                 that death was seen in, which is the room hung up in or another
//                 of its land. A yes of this kind is the weaker one
//                 (HangupDeathArrivalRooms.ArrivalsOn).
public readonly record struct HangupEntryRoomReading(
    bool? Says, RoomKey? TakenFor = null, bool Albion = false, RoomKey? LearnedFrom = null)
{
    public bool Learned => LearnedFrom is not null;
}
