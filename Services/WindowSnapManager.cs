using Avalonia;
using Avalonia.Controls;

namespace MudPlay.Services;

// Magnetic window snapping for MudPlay's panel windows — the ones reachable from
// the main terminal's right-click menu / the View menu (main, conversation, party,
// buff watchdog, player workshop, navigation, spell book, session stats). Child
// windows opened from within a panel deliberately don't participate.
//
// Behaviour:
//   - Drag any non-main panel and, when an edge lands within SnapThreshold of
//     another panel's opposite edge (with overlap on the perpendicular axis), it
//     snaps flush against it.
//   - Drag the MAIN window and the whole transitively-snapped cluster moves with
//     it, keeping each panel's relative position.
//   - Grab any non-main panel and it pulls off freely — adjacency is re-derived
//     from live positions every move, so a window dragged away simply stops being
//     part of the cluster. There is no snap graph to maintain.
//
// Avalonia only surfaces post-move PositionChanged (no drag-in-progress hook), so
// a snap is a small correction after the OS finishes the move, not a live pull.
// Positions are physical desktop pixels; sizes are device-independent, so a rect
// is built by scaling the frame size to the window's screen — the same convention
// WindowLayoutStore uses.
//
// Enable is read live from the Global "Snap windows together" setting; when off the
// manager still tracks positions but never moves a window.
public sealed class WindowSnapManager
{
    // Snap when a dragged edge lands within this many physical pixels of another
    // panel's opposite edge.
    internal const int SnapThreshold = 12;
    // Two panels count as clustered when their edges sit within this gap (snapping
    // lands them flush at 0; the slack absorbs rounding + frame-decoration jitter).
    internal const int AdjacencyTolerance = 4;
    private const string MainId = "main";

    private static readonly HashSet<string> Participants = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "conversation", "buffwatchdog", "party",
        "workshop", "navigation", "spellbook", "session-stats",
    };

    private readonly Func<bool> _enabled;
    private readonly LogService? _log;

    private sealed class Panel
    {
        public required Window Window;
        public required string Id;
        public bool IsMain;
        public PixelPoint LastPos;
        // Set just before we move the window ourselves; the resulting
        // PositionChanged that matches it is swallowed so our own moves don't
        // recurse into snapping / group logic.
        public PixelPoint? Expected;
    }

    private readonly Dictionary<string, Panel> _open = new(StringComparer.OrdinalIgnoreCase);

    // Consecutive main-window moves within this gap are treated as one drag; a longer
    // pause ends it, and the next main move recaptures the cluster.
    private const double DragGapMs = 300;
    // Cluster membership is captured off main's PRE-move rect (flush with the children,
    // so a fast first drag step can't outrun it), with this generous tolerance so a
    // title-bar-sized stale-LastPos offset on the main window can't drop a below-main
    // panel out of the cluster. Non-snapped panels sit far past it.
    private const int DragAdjacencyTolerance = 40;
    // The snapped cluster captured at the START of the current main drag: member id →
    // its fixed offset from the main window. Held rigid for the whole drag so async WM
    // lag (setting Position is a request the WM fulfils later) can't shake a member
    // loose mid-drag. Null between drags.
    private Dictionary<string, PixelPoint>? _clusterOffsets;
    private DateTime _lastClusterMoveAt;

    public WindowSnapManager(Func<bool> enabled, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
        _log = log;
    }

    // TEMP diagnostic — remove once snapping is confirmed working. One-line geometry
    // dump for a panel: live position, tracked (intended) position, and physical rect.
    private static string Geo(Panel p)
        => $"live={p.Window.Position} last={p.LastPos} frame={p.Window.FrameSize} rect={RectOf(p)}";

    // Called by WindowLayoutStore.AttachWindow for every window; non-participants
    // are ignored. Registration is per Window instance (toggle windows re-open as a
    // fresh instance), so the handlers wire once.
    public void Register(Window window, string id)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (id is null || !Participants.Contains(id)) return;

        window.Opened += (_, _) =>
        {
            Panel panel = new()
            {
                Window = window,
                Id = id,
                IsMain = string.Equals(id, MainId, StringComparison.OrdinalIgnoreCase),
                LastPos = window.Position,
            };
            _open[id] = panel;
            _log?.Debug("WindowSnap", $"track {id} (main={panel.IsMain}) {Geo(panel)}");
        };
        window.Closed += (_, _) =>
        {
            if (_open.TryGetValue(id, out Panel? p) && ReferenceEquals(p.Window, window))
                _open.Remove(id);
        };
        window.PositionChanged += (_, e) => OnMoved(window, id, e.Point);
    }

    // WindowLayoutStore calls this right before it repositions a window itself (a
    // profile-load re-layout), so the snap manager swallows that move instead of
    // treating a main-window reposition as a user drag and hauling the cluster.
    public void ExpectMove(string id, PixelPoint pos)
    {
        if (_open.TryGetValue(id, out Panel? p)) p.Expected = pos;
    }

    private void OnMoved(Window window, string id, PixelPoint newPos)
    {
        if (!_open.TryGetValue(id, out Panel? p) || !ReferenceEquals(p.Window, window))
            return;

        // Our own programmatic move (snap correction, cluster drag, or a layout
        // restore that called ExpectMove) — record and swallow.
        if (p.Expected is { } exp && IsClose(exp, newPos))
        {
            _log?.Debug("WindowSnap", $"swallow {id} own-move exp={exp} got={newPos}");
            p.Expected = null;
            p.LastPos = newPos;
            return;
        }

        if (!_enabled() || window.WindowState != WindowState.Normal)
        {
            p.LastPos = newPos;
            return;
        }

        if (p.IsMain)
        {
            DragCluster(newPos, p.LastPos);
            p.LastPos = newPos;
            return;
        }

        // A cluster member's own PositionChanged arriving mid main-drag — our shift, or
        // the WM still catching up to it — must NOT read as the user pulling it off, and
        // must NOT overwrite the intended LastPos MoveTo set with a lagging value. Only
        // re-snap a member once the main drag has settled.
        if (_clusterOffsets is not null && _clusterOffsets.ContainsKey(id)
            && (DateTime.UtcNow - _lastClusterMoveAt).TotalMilliseconds < DragGapMs)
        {
            _log?.Debug("WindowSnap", $"suppress {id} mid-drag @ {newPos}");
            return;
        }

        // On a snap, MoveTo already recorded the SNAPPED position as LastPos — don't
        // clobber it with the raw drop position, or the cluster offset later captured
        // from it would be off by the snap correction (a jump on the next main drag).
        if (!SnapAndSettle(p, newPos)) p.LastPos = newPos;
    }

    // Main moved by the user. On the first move of a drag, capture the snapped cluster
    // and each member's offset from main; then hold every member at that fixed offset
    // for the rest of the drag. Offsets (not per-step adjacency) keep the cluster rigid
    // even while the WM lags behind our position requests.
    private void DragCluster(PixelPoint newMainPos, PixelPoint mainLastPos)
    {
        DateTime now = DateTime.UtcNow;
        bool newDrag = _clusterOffsets is null
            || (now - _lastClusterMoveAt).TotalMilliseconds > DragGapMs;
        _lastClusterMoveAt = now;

        if (newDrag)
        {
            // MEMBERSHIP: measure adjacency against main's PRE-move rect (LastPos — it's
            // still pre-move here; OnMoved updates it after us) so a fast first drag step
            // can't leave main sitting past the child before we capture. The generous
            // DragAdjacencyTolerance also absorbs a title-bar-sized stale-LastPos offset
            // on main (a WM can settle its frame without a PositionChanged) so a below-
            // main panel isn't dropped. Children use their LIVE at-rest rect.
            //
            // OFFSET: relative to main's LIVE position (newMainPos), never LastPos — so
            // the stale offset never shifts the cluster. The capturing move is a no-op;
            // members then track main precisely for the rest of the drag.
            Dictionary<string, PixelRect> rects = new(StringComparer.OrdinalIgnoreCase);
            foreach (Panel p in _open.Values)
                rects[p.Id] = p.IsMain ? RectAt(p, p.LastPos) : RectOf(p);

            _log?.Debug("WindowSnap",
                $"drag-START: main last={mainLastPos} live={newMainPos} delta={newMainPos - mainLastPos}");
            PixelRect mainRect = rects[MainId];
            foreach ((string pid, PixelRect r) in rects)
            {
                if (string.Equals(pid, MainId, StringComparison.OrdinalIgnoreCase)) continue;
                _log?.Debug("WindowSnap",
                    $"  cand {pid}: rect={r} adjMain={Adjacent(mainRect, r, DragAdjacencyTolerance)}");
            }

            _clusterOffsets = new(StringComparer.OrdinalIgnoreCase);
            foreach (string cid in ConnectedFrom(MainId, rects, DragAdjacencyTolerance))
            {
                if (string.Equals(cid, MainId, StringComparison.OrdinalIgnoreCase)) continue;
                if (_open.TryGetValue(cid, out Panel? m))
                    _clusterOffsets[cid] = m.Window.Position - newMainPos;
            }
            _log?.Debug("WindowSnap",
                $"  cluster=[{string.Join(", ", _clusterOffsets.Select(kv => $"{kv.Key}@{kv.Value}"))}]");
        }
        else
        {
            _log?.Debug("WindowSnap", $"drag-move: live={newMainPos} members={_clusterOffsets!.Count}");
        }

        foreach ((string cid, PixelPoint offset) in _clusterOffsets!)
            if (_open.TryGetValue(cid, out Panel? m))
                MoveTo(m, newMainPos + offset);
    }

    // A non-main panel was dragged: snap its nearest edge to another panel if one is
    // within reach. Returns true (and records the snapped position as LastPos via
    // MoveTo) when it snapped; false when it stays where the user dropped it.
    private bool SnapAndSettle(Panel c, PixelPoint newPos)
    {
        PixelRect cr = RectAt(c, newPos);
        List<PixelRect> others = new();
        foreach (Panel v in _open.Values)
        {
            if (ReferenceEquals(v, c) || v.Window.WindowState != WindowState.Normal) continue;
            others.Add(RectOf(v));   // intended position — a reference window isn't being dragged
        }

        (int axis, int shift) = ComputeSnap(cr, others, SnapThreshold);
        if (axis == 0)
        {
            _log?.Debug("WindowSnap", $"drag {c.Id} raw={newPos} rect={cr} → no-snap");
            return false;
        }

        PixelPoint snapped = axis == 1
            ? new PixelPoint(newPos.X + shift, newPos.Y)
            : new PixelPoint(newPos.X, newPos.Y + shift);
        _log?.Debug("WindowSnap",
            $"drag {c.Id} raw={newPos} → snap axis{axis} shift{shift} = {snapped}");
        if (snapped == newPos) return false;
        MoveTo(c, snapped);
        return true;
    }

    private void MoveTo(Panel p, PixelPoint pos)
    {
        p.Expected = pos;
        p.LastPos = pos;
        p.Window.Position = pos;
    }

    // ----- Pure geometry (unit-tested via internals) -----------------------

    // The single closest edge snap for c against others, or (0, 0) when nothing is
    // within threshold. axis 1 = adjust X, 2 = adjust Y; shift is the signed delta.
    // Adjacency only (opposite edges touch), so panels tile beside/above each other
    // rather than overlapping — and only one axis moves (no perpendicular align).
    internal static (int Axis, int Shift) ComputeSnap(
        PixelRect c, IReadOnlyList<PixelRect> others, int threshold)
    {
        int bestAxis = 0, bestShift = 0, bestAbs = threshold + 1;

        void Consider(int shift, int axis)
        {
            int abs = Math.Abs(shift);
            if (abs < bestAbs) { bestAbs = abs; bestShift = shift; bestAxis = axis; }
        }

        foreach (PixelRect v in others)
        {
            if (RangesOverlap(c.Y, c.Bottom, v.Y, v.Bottom))   // side by side → snap X
            {
                Consider(v.X - c.Right, 1);        // c to the left of v
                Consider(v.Right - c.X, 1);        // c to the right of v
            }
            if (RangesOverlap(c.X, c.Right, v.X, v.Right))     // stacked → snap Y
            {
                Consider(v.Y - c.Bottom, 2);       // c above v
                Consider(v.Bottom - c.Y, 2);       // c below v
            }
        }
        return (bestAxis, bestShift);
    }

    // Two rects are clustered when a pair of opposite edges touches (within tol) and
    // they overlap on the perpendicular axis — flush neighbours, not mere overlap.
    internal static bool Adjacent(PixelRect a, PixelRect b, int tol)
    {
        bool vOverlap = a.Y < b.Bottom && b.Y < a.Bottom;
        bool hOverlap = a.X < b.Right && b.X < a.Right;
        bool hTouch = Math.Abs(a.Right - b.X) <= tol || Math.Abs(b.Right - a.X) <= tol;
        bool vTouch = Math.Abs(a.Bottom - b.Y) <= tol || Math.Abs(b.Bottom - a.Y) <= tol;
        return (hTouch && vOverlap) || (vTouch && hOverlap);
    }

    // BFS the adjacency graph from start; returns every reachable id (including start).
    internal static HashSet<string> ConnectedFrom(
        string start, IReadOnlyDictionary<string, PixelRect> rects, int tol)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (!rects.ContainsKey(start)) return seen;

        Queue<string> queue = new();
        queue.Enqueue(start);
        seen.Add(start);
        while (queue.Count > 0)
        {
            PixelRect a = rects[queue.Dequeue()];
            foreach ((string id, PixelRect b) in rects)
            {
                if (seen.Contains(id) || !Adjacent(a, b, tol)) continue;
                seen.Add(id);
                queue.Enqueue(id);
            }
        }
        return seen;
    }

    private static bool RangesOverlap(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;

    private static bool IsClose(PixelPoint a, PixelPoint b)
        => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2;

    // A panel's rect at its LIVE position. Used for snap references and the drag-start
    // cluster capture — both happen while the panel is at rest, so live is exactly where
    // it sits (and, unlike LastPos, can't be left stale by a silent WM frame settle).
    private static PixelRect RectOf(Panel p) => RectAt(p, p.Window.Position);

    private static PixelRect RectAt(Panel p, PixelPoint pos)
    {
        Window w = p.Window;
        double scale = w.Screens?.ScreenFromWindow(w)?.Scaling ?? w.RenderScaling;
        if (scale <= 0) scale = 1;
        Size size = w.FrameSize ?? w.Bounds.Size;
        return new PixelRect(pos, new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale))));
    }
}
