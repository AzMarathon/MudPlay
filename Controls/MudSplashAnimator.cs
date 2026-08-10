using System;
using Avalonia.Threading;
using MudPlay.Terminal;

namespace MudPlay.Controls;

// Startup "attract" splash drawn on the terminal until a session begins. A
// figure in our line of sight winds up and hurls a mud ball at the lens like an
// overhand pitch; it splats across the view, then slides slowly down and off the
// bottom — revealing him again as he throws the next, so the loop is seamless.
// "MudPlay" + "Created By Fujin" + a hint sit in the top rows (mud never touches
// them). When animation is disabled those header lines still show, on their own.
//
// Everything is drawn into a STANDALONE TerminalScreen — never the session
// emulator — so nothing here reaches the scrollback log.
public sealed class MudSplashAnimator : IDisposable
{
    public const int LoopFrames = 140;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);

    // Phase boundaries (frame index within the loop). The slide gets the bulk of
    // the frames so the mud runs down the "lens" slowly.
    private const int WindupEnd = 13;   // arm draws up and back
    private const int ThrowEnd  = 19;   // overhand swing; ball leaves the hand
    private const int FlyEnd    = 27;   // ball grows as it flies at the camera
    private const int SplatEnd  = 34;   // mud floods the viewport
    private const int SlideEnd  = 130;  // it slides down and off (slow) — 96 frames
    // 131..139: figure stands clean, ready to throw → loops back to windup.

    private const int TitleRows = 4;    // reserved for header; mud stays below.

    // ----- palette ----------------------------------------------------------
    private static readonly CellAttributes BgAttr    = CellAttributes.Default;
    private static readonly CellAttributes BrownDark = Attr(64, 44, 26);
    private static readonly CellAttributes Brown     = Attr(104, 72, 40);
    private static readonly CellAttributes BrownLite = Attr(150, 108, 62);
    private static readonly CellAttributes MudGreen  = Attr(86, 96, 44);
    private static readonly CellAttributes Fleck     = Attr(176, 134, 80);
    private static readonly CellAttributes Figure    = Attr(66, 56, 46);
    private static readonly CellAttributes FigureLit = Attr(98, 84, 66);
    private static readonly CellAttributes Title     = Attr(222, 190, 120);
    private static readonly CellAttributes Byline    = Attr(150, 128, 90);
    private static readonly CellAttributes Hint      = Attr(120, 120, 120);

    public TerminalScreen Screen { get; private set; }
    public bool IsPlaying { get; private set; }

    // Whether the mud figure animates. When false, only the header lines render
    // (a static branding screen) and the frame timer never runs.
    public bool Animate { get; }

    // Raised on the UI thread after each frame is drawn — the control invalidates.
    public event Action? FrameAdvanced;

    private readonly DispatcherTimer _timer;
    private int _frame;
    private bool _disposed;

    public MudSplashAnimator(int cols, int rows, bool animate)
    {
        Animate = animate;
        Screen = new TerminalScreen(Clamp(cols, 40, 400), Clamp(rows, 15, 200));
        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += OnTick;
        RenderFrame(0);
    }

    public void Resize(int cols, int rows)
    {
        int c = Clamp(cols, 40, 400), r = Clamp(rows, 15, 200);
        if (Screen.Cols == c && Screen.Rows == r) return;
        Screen = new TerminalScreen(c, r);
        RenderFrame(_frame);
    }

    public void Start()
    {
        if (IsPlaying || _disposed) return;
        IsPlaying = true;
        // Disabled → render the static header once; no ticking.
        if (Animate) _timer.Start();
        else RenderFrame(0);
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _frame = (_frame + 1) % LoopFrames;
        RenderFrame(_frame);
        FrameAdvanced?.Invoke();
    }

    // ----- frame composition ------------------------------------------------

    private void RenderFrame(int f)
    {
        TerminalScreen s = Screen;
        s.ClearAll(BgAttr);
        DrawHeader(s);

        if (!Animate) { s.Bump(); return; }

        if (f < ThrowEnd)         DrawFigure(s, HandInWindupThrow(s, f), ballAtHand: f < ThrowEnd - 1);
        else if (f < FlyEnd)      { DrawFigure(s, NeutralHand(s), ballAtHand: false); DrawFlyingBall(s, f); }
        else if (f <= SplatEnd)   DrawSplat(s, f);
        else if (f <= SlideEnd)   { DrawFigure(s, NeutralHand(s), ballAtHand: false); DrawSlide(s, f); }
        else                      DrawFigure(s, NeutralHand(s), ballAtHand: false);

        s.Bump();
    }

    private static void DrawHeader(TerminalScreen s)
    {
        const string title  = "M u d P l a y";
        const string byline = "Created By Fujin";
        const string hint   = "Load a profile or connect to a BBS to begin";
        PutStr(s, (s.Cols - title.Length) / 2, 0, title, Title);
        PutStr(s, (s.Cols - byline.Length) / 2, 1, byline, Byline);
        PutStr(s, (s.Cols - hint.Length) / 2, 3, hint, Hint);
    }

    // ----- the pitcher ------------------------------------------------------

    private int FootRow(TerminalScreen s) => s.Rows - 2;

    // The throwing hand at rest (arm down at the side).
    private (int X, int Y) NeutralHand(TerminalScreen s)
    {
        int cx = s.Cols / 2, foot = FootRow(s);
        return (cx + 3, foot - 4);
    }

    // Hand position through the windup + overhand throw. Windup lifts it up and
    // back; the throw arcs it over the top and forward toward the lens.
    private (int X, int Y) HandInWindupThrow(TerminalScreen s, int f)
    {
        int cx = s.Cols / 2, foot = FootRow(s);
        if (f <= WindupEnd)
        {
            double t = f / (double)WindupEnd;
            return (cx + 3 + (int)Math.Round(t), foot - 4 - (int)Math.Round(t * 5));
        }
        double u = (f - WindupEnd) / (double)(ThrowEnd - WindupEnd);
        int hx = (int)Math.Round(Lerp(cx + 4, cx - 1, u));
        int hy = (int)Math.Round(Lerp(foot - 9, foot - 7, u) - Math.Sin(u * Math.PI) * 1.5);
        return (hx, hy);
    }

    private void DrawFigure(TerminalScreen s, (int X, int Y) hand, bool ballAtHand)
    {
        int cx = s.Cols / 2, foot = FootRow(s);
        if (foot - 9 < TitleRows) return;   // not tall enough to place the figure

        // Body — centred silhouette (CP437 block glyphs).
        PutStr(s, cx - 2, foot - 8, "▄██▄", Figure);   // head
        PutStr(s, cx - 2, foot - 7, "▐██▌", Figure);
        PutStr(s, cx - 3, foot - 6, "▄████▄", Figure);  // shoulders
        PutStr(s, cx - 2, foot - 5, "████", FigureLit); // chest (lit)
        PutStr(s, cx - 2, foot - 4, "████", Figure);    // torso
        PutStr(s, cx - 2, foot - 3, "▐██▌", Figure);    // waist
        PutStr(s, cx - 2, foot - 2, "█  █", Figure);    // legs
        PutStr(s, cx - 2, foot - 1, "▀  ▀", Figure);    // feet

        // Left (non-throwing) arm — hangs at the side so he isn't one-armed.
        DrawLine(s, cx - 3, foot - 6, cx - 4, foot - 3, '▓', Figure);
        PutCell(s, cx - 4, foot - 3, '▄', Figure);       // left hand

        // Right (throwing) arm: a line from the right shoulder to the hand.
        DrawLine(s, cx + 2, foot - 6, hand.X, hand.Y, '▓', FigureLit);
        if (ballAtHand)
        {
            PutCell(s, hand.X, hand.Y, '●', BrownLite);
            PutCell(s, hand.X, hand.Y - 1, '▒', Brown);
        }
    }

    // Ball leaves the release point and grows as it nears the camera (view centre).
    private void DrawFlyingBall(TerminalScreen s, int f)
    {
        (int rx, int ry) = HandInWindupThrow(s, ThrowEnd);   // release point
        int cx = s.Cols / 2, cy = (s.Rows + TitleRows) / 2;
        double p = (f - ThrowEnd) / (double)(FlyEnd - ThrowEnd);
        int bx = (int)Math.Round(Lerp(rx, cx, p));
        int by = (int)Math.Round(Lerp(ry, cy, p));
        int rad = 1 + (int)Math.Round(p * 4);
        Blob(s, bx, by, rad);
    }

    // Full radial flood from the view centre; ragged, organic edge.
    private void DrawSplat(TerminalScreen s, int f)
    {
        int cx = s.Cols / 2, cy = (s.Rows + TitleRows) / 2;
        double maxR = MaxRadius(s);
        double r = maxR * EaseOut((f - FlyEnd) / (double)(SplatEnd - FlyEnd));
        for (int y = TitleRows; y < s.Rows; y++)
        for (int x = 0; x < s.Cols; x++)
        {
            double it = SplatAt(x, y, cx, cy, r);
            if (it > 0) PutCell(s, x, y, MudChar(it, x, y), MudAttr(it, x, y));
        }
        Flecks(s, cx, cy, r);
    }

    // The mud runs down and off the bottom (slow), the top clearing first so the
    // figure (at the bottom) is revealed last. Crucially each COLUMN slides at its
    // own pace, so the receding top edge stays ragged and it reads as mud running,
    // not a solid panel dropping. Sparse residue streaks cling in the cleared band,
    // denser just above each column's mud edge.
    private void DrawSlide(TerminalScreen s, int f)
    {
        int cx = s.Cols / 2, cy = (s.Rows + TitleRows) / 2;
        double maxR = MaxRadius(s);
        double p = (f - SplatEnd) / (double)(SlideEnd - SplatEnd);   // 0..1
        // Push far enough that even the SLOWEST column clears the splat's whole
        // vertical extent by the end (so the last frame is truly clean — no abrupt
        // cut before the loop). The margin over-clears; it must exceed 0.5/minSpeed.
        double baseOff = EaseInSoft(p) * (s.Rows + maxR) * 1.3;
        double residueFade = 1.0 - p;   // streaks wash away as the mud finishes running off

        for (int x = 0; x < s.Cols; x++)
        {
            // Wide per-column spread: some rivulets crawl, others rush — so the
            // lines running down the "lens" are varied rather than a uniform sheet.
            double speed = 0.42 + 1.25 * Noise(x, 991);
            int off = (int)Math.Round(baseOff * speed);
            int edgeY = TitleRows + off;                  // top of this column's mud

            // Per-column character of the trailing rivulet: how runny, how thick.
            double runny = Noise(x, 13);
            char streakCh = runny > 0.85 ? '║' : '│';     // a few thick rivulets

            for (int y = TitleRows; y < s.Rows; y++)
            {
                if (y < edgeY)
                {
                    // The trail the mud left running down — denser near the edge,
                    // thinning up, varied per column, fading as the slide finishes.
                    double nearEdge = 1.0 - (edgeY - y) / (double)Math.Max(1, off);
                    double prob = (0.05 + 0.55 * nearEdge) * residueFade * (0.35 + runny);
                    if (runny > 0.25 && Noise(x, y) < prob)
                    {
                        CellAttributes c = y > edgeY - 2 ? BrownLite : y > edgeY - 6 ? Brown : BrownDark;
                        PutCell(s, x, y, y > edgeY - 2 && Noise(x, y * 3) > 0.7 ? '•' : streakCh, c);
                    }
                    continue;
                }
                int srcY = y - off;
                double it = SplatAt(x, srcY, cx, cy, maxR);
                if (it > 0) PutCell(s, x, y, MudChar(it, x, srcY), MudAttr(it, x, srcY));
            }
        }
    }

    // ----- primitives -------------------------------------------------------

    private static double MaxRadius(TerminalScreen s) =>
        Math.Max(s.Cols, (s.Rows - TitleRows) * 2) * 0.66;

    // Mud-sheet intensity at a cell for a splat of radius r (0 outside). Cells are
    // ~1:2, so y is scaled to keep the blob roughly round.
    private static double SplatAt(int x, int y, int cx, int cy, double r)
    {
        double dx = x - cx, dy = (y - cy) * 2.0;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double edge = r * (0.80 + 0.32 * Noise(x, y));
        return d > edge ? 0 : 1.0 - d / Math.Max(1.0, edge);
    }

    private static void Blob(TerminalScreen s, int cx, int cy, int rad)
    {
        for (int y = cy - rad; y <= cy + rad; y++)
        for (int x = cx - rad; x <= cx + rad; x++)
        {
            if (y < TitleRows || y >= s.Rows || x < 0 || x >= s.Cols) continue;
            double dx = x - cx, dy = (y - cy) * 2.0;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > rad + 0.4) continue;
            double it = 1.0 - d / Math.Max(1.0, rad + 0.4);
            PutCell(s, x, y, MudChar(it, x, y), MudAttr(it, x, y));
        }
    }

    private static void Flecks(TerminalScreen s, int cx, int cy, double r)
    {
        for (int i = 0; i < 16; i++)
        {
            double a = i * 2.399963;   // golden-angle spray
            double dist = r * (1.02 + 0.35 * Noise(i, i * 7));
            int x = cx + (int)Math.Round(Math.Cos(a) * dist);
            int y = cy + (int)Math.Round(Math.Sin(a) * dist * 0.5);
            if (y < TitleRows || y >= s.Rows || x < 0 || x >= s.Cols) continue;
            PutCell(s, x, y, Noise(x, y) > 0.5 ? '·' : '•', Noise(y, x) > 0.6 ? MudGreen : Brown);
        }
    }

    private static char MudChar(double it, int x, int y)
    {
        double v = Math.Clamp(it + (Noise(x, y) - 0.5) * 0.3, 0, 1);
        return v > 0.72 ? '█' : v > 0.48 ? '▓' : v > 0.24 ? '▒' : '░';
    }

    private static CellAttributes MudAttr(double it, int x, int y)
    {
        double n = Noise(y, x);
        if (n > 0.86) return MudGreen;
        if (it > 0.78) return n > 0.5 ? BrownLite : Fleck;
        if (it > 0.45) return Brown;
        return BrownDark;
    }

    // A rough integer line (used for the throwing arm).
    private static void DrawLine(TerminalScreen s, int x0, int y0, int x1, int y1, char ch, CellAttributes attr)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            PutCell(s, x0, y0, ch, attr);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void PutStr(TerminalScreen s, int x, int y, string text, CellAttributes attr)
    {
        if (y < 0 || y >= s.Rows) return;
        for (int i = 0; i < text.Length; i++)
        {
            int cx = x + i;
            if (cx < 0 || cx >= s.Cols) continue;
            if (text[i] != ' ') s.Put(cx, y, new Cell(text[i], attr));
        }
    }

    private static void PutCell(TerminalScreen s, int x, int y, char ch, CellAttributes attr)
    {
        if (x < 0 || x >= s.Cols || y < TitleRows || y >= s.Rows) return;
        s.Put(x, y, new Cell(ch, attr));
    }

    // Stable per-cell noise (no per-frame jitter → mud grows/slides smoothly).
    private static double Noise(int x, int y)
    {
        unchecked
        {
            int h = x * 73856093 ^ y * 19349663;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h >> 8) & 0xFFFF) / 65535.0;
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double EaseOut(double p) => 1.0 - (1.0 - p) * (1.0 - p);
    // Gentle acceleration: a touch sticky at first, then a steady slow run.
    private static double EaseInSoft(double p) => p * p * (0.6 + 0.4 * p);
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static CellAttributes Attr(byte r, byte g, byte b) =>
        CellAttributes.Default.WithForeground(TerminalColor.Rgb(r, g, b));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
