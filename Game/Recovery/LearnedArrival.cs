using MudPlay.Game.Map;

namespace MudPlay.Game.Recovery;

// A room the dead of some room are taken to arrive in because a death was seen
// to end there (LearnedArrivalLookup).
//   Arrival — the room the death ended in.
//   SeenIn  — the room that death was seen in: the room asked about itself, or
//             another room of the same land, whose reading stands for it.
public readonly record struct LearnedArrival(RoomKey Arrival, RoomKey SeenIn);
