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
public readonly record struct DetourResume(DetourResumeKind Kind, Loop? Loop = null, RoomKey? WalkDestination = null)
{
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
        if (includeWalk && walker.State is not WalkState.Idle && walker.Destination is { } dest)
            return new DetourResume(DetourResumeKind.Walk, WalkDestination: dest);
        return new DetourResume(DetourResumeKind.None);
    }

    public void Stop(AutoWalkManager walker, LoopRunner loops, AutoLairManager lair, string reason)
    {
        if (lair.IsActive) lair.Stop(reason);
        if (loops.State is not LoopState.Idle) loops.Stop(reason);
        if (Kind == DetourResumeKind.Walk) walker.Stop(reason);
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
                if (WalkDestination is { } dest) walker.WalkTo(dest, planThroughAcquirableGates: true);
                break;
        }
    }
}
