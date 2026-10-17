namespace MudPlay.Models.Settings;

// The distinct navigation polylines the map draws, each independently colourable
// + thickness-adjustable (Settings → General). MapControl builds one pen per
// kind (BuildNavPen).
public enum NavLineKind
{
    // The point-to-point walk-to / go-to route.
    Goto,
    // An active loop's route.
    Loop,
    // The "where Run would walk" preview for a queued go-to.
    Preview,
    // The in-progress loop-builder / loop-approach preview.
    LoopBuilder,
    // An Auto-Lair run's route.
    AutoLair,
    // The party leader's route we're following, rebuilt from their @path reply.
    // Cyan by default so it never reads as a route we're driving.
    FollowRoute,
}
