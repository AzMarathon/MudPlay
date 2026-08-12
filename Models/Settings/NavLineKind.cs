namespace MudPlay.Models.Settings;

// The distinct navigation polylines the map draws, each independently colourable
// + thickness-adjustable (Settings → General). Names mirror MapControl's pens.
public enum NavLineKind
{
    // The point-to-point walk-to / go-to route (MapControl.WalkPathPen).
    Goto,
    // An active loop's route (MapControl.LoopPathPen).
    Loop,
    // The "where Run would walk" preview for a queued go-to (MapControl.PreviewPathPen).
    Preview,
    // The in-progress loop-builder / loop-approach preview (MapControl.LoopBuilderPen).
    LoopBuilder,
    // An Auto-Lair run's route (MapControl.AutoLairWalkPen).
    AutoLair,
}
