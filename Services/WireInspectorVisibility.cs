namespace MudPlay.Services;

// Which Wire Inspector panes are "on" — read by BugReportBuilder to decide whether to
// attach the raw / classified wire. **Default ON** for both (Stripped isn't captured),
// so a report carries the raw wire + the engine's read of each combat line out of the
// box; the WireInspectorViewModel's checkboxes edit this, and the choice STICKS after
// the window closes (it's a preference, not "is the pane currently on screen"). Resets
// to the ON default on app restart.
public sealed class WireInspectorVisibility
{
    public bool RawVisible { get; set; } = true;
    public bool ClassifiedVisible { get; set; } = true;
}
