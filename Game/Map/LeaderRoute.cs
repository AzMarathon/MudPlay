namespace MudPlay.Game.Map;

// The route another player is walking, rebuilt on our side from their @path reply.
// Rooms runs from their reported room to the destination; Steps is the same route as
// the walker would expand it (door / lever detours included), which is what OurSteps
// counts and what the CURRENT NAV rail lists. OurSteps is the walker-step
// count of the route we planned; TheirSteps what their reply says is left. Matches is
// true when the two agree (within LeaderRouteResolver.MatchTolerance); Variant names
// the planning choice that produced the route ("our usual route", "with teleports", …).
public sealed record LeaderRoute(
    IReadOnlyList<RoomKey> Rooms,
    IReadOnlyList<WalkStep> Steps,
    int OurSteps,
    int TheirSteps,
    bool Matches,
    string Variant);
