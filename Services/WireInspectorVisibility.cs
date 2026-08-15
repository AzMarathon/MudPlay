namespace MudPlay.Services;

// Tracks which Wire Inspector panes the user currently has up, so a bug report can
// attach the raw / classified wire buffers only when the user is actively debugging
// with them visible (they're large, and irrelevant to most reports otherwise). The
// WireInspectorViewModel pushes its pane state here while open and resets both to
// false when the window closes.
public sealed class WireInspectorVisibility
{
    public bool RawVisible { get; set; }
    public bool ClassifiedVisible { get; set; }
}
