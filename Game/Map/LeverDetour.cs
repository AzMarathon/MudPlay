namespace MudPlay.Game.Map;

// Where the detour to a lever exit's actions sits in an expanded route. Start is
// the step taken from the room the route leaves for the lever rooms; [Start, End)
// are the detour's own steps, which come back to that room for a round trip, and
// Cross is the step through the opened exit (GateRoom, GateDir). Between End and
// Cross the route goes on to the gate. A one-way detour (OneWay) leaves the route
// for good and ends at the gate, so End is Cross.
public readonly record struct LeverDetour(int Start, int End, int Cross, RoomKey GateRoom, Direction GateDir, bool OneWay)
{
    public LeverDetour Offset(int by) => this with { Start = Start + by, End = End + by, Cross = Cross + by };

    // The step joins the route after a lever action of this detour and before its
    // crossing: started there, the run crosses an exit nothing opened.
    public bool Inside(int step) => step > Start && step <= Cross;
}
