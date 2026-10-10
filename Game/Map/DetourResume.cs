namespace MudPlay.Game.Map;

public enum DetourResumeKind
{
    None,
    Walk,
    Loop,
    Lair,
}

// The movement engine an errand detour interrupts, and how to pick it back up —
// shared by the auto-deposit reroute and the sell detour. Stop-and-restart, not a
// gate-pause: a MovementCoordinator gate would block the detour walk itself.
//
// A walk is held as its journey, not the room the walker was heading for: caught on
// a side trip that room is a giver's or a shop's, and a walk restarted from a bare
// destination forgets the route it was on (the gates a route card went round, how
// it takes teleports).
public readonly record struct DetourResume(DetourResumeKind Kind, Loop? Loop = null, WalkJourney? Journey = null)
{
    public RoomKey? WalkDestination => Journey?.Destination;

    // The engine running now. Priority Lair → Loop → Walk: Auto-Lair drives the walker
    // and a loop drives it while approaching, so the topmost active engine is the real
    // activity. includeWalk off leaves a plain walk-to out (auto-deposit only reroutes
    // loops and lairs).
    public static DetourResume Snapshot(
        AutoWalkManager walker, LoopRunner loops, AutoLairManager lair, bool includeWalk)
    {
        if (lair.IsActive) return new DetourResume(DetourResumeKind.Lair);
        if (loops.State is not LoopState.Idle && loops.CurrentLoop is { } loop)
            return new DetourResume(DetourResumeKind.Loop, loop);
        if (includeWalk && walker.State is not WalkState.Idle && walker.Journey is { } journey)
            return new DetourResume(DetourResumeKind.Walk, Journey: journey);
        return new DetourResume(DetourResumeKind.None);
    }

    public void Stop(AutoWalkManager walker, LoopRunner loops, AutoLairManager lair, string reason)
    {
        // Said to be coming back: an event run on the engine waits out the detour
        // instead of reading the stop as the user calling it off.
        if (lair.IsActive) lair.Stop(reason, willResume: true);
        if (loops.State is not LoopState.Idle) loops.Stop(reason, willResume: true);
        if (Kind == DetourResumeKind.Walk) walker.Stop(reason, willResume: true);
    }

    public void Resume(AutoWalkManager walker, LoopRunner loops, AutoLairManager lair)
    {
        switch (Kind)
        {
            case DetourResumeKind.Lair:
                lair.Start();
                break;
            case DetourResumeKind.Loop:
                // ResumeAfterDetour, not Start: the loop's first-waypoint reset (session
                // stats + party @reset) already fired at the user's original Start, so a
                // detour's continuation mustn't re-fire it. throughGates: if the walk back
                // couldn't land us inside the grind area, the re-approach still plans
                // through the acquirable gates to re-enter it.
                if (Loop is { } loop) loops.ResumeAfterDetour(loop, throughGates: true);
                break;
            case DetourResumeKind.Walk:
                if (Journey is { } journey) walker.ResumeJourney(journey, planThroughAcquirableGates: true);
                break;
        }
    }
}
