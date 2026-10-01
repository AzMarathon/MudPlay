using System;
using MudPlay.Game.Map;

namespace MudPlay.Game.Train;

// What a train trip's tolls and fares come to from a room (on to each trainer in
// turn, then back to where the run left off), and whether every leg also has a
// toll-free way round.
public sealed record TrainTripTolls(Func<RoomKey, long> CopperFrom, Func<RoomKey, bool> AvoidableFrom);
