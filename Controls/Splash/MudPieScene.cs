using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// An arm swings in from the right holding a mud pie, cocks back, and hurls it
// at the lens — SPLAT — then the mud oozes off clean.
public sealed class MudPieScene : SplashScene
{
    public override int LoopFrames => 108;

    private const int EnterEnd = 14;   // arm + pie slide in from the right
    private const int CockEnd  = 26;   // arm pulls back for the throw
    private const int ThrowEnd = 34;   // swing forward; pie is released
    private const int FlyEnd   = 42;   // pie flies at the lens, growing
    private const int SplatEnd = 52;   // mud floods the view
    private const int SlideEnd = 102;  // oozed off
    // 102..107: clear.

    private static readonly CellAttributes Skin   = SplashCanvas.Rgb(210, 170, 130);
    private static readonly CellAttributes Sleeve = SplashCanvas.Rgb(120, 90, 160);
    private static readonly CellAttributes Plate  = SplashCanvas.Rgb(200, 200, 208);

    public override void Render(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        if (f > SplatEnd && f <= SlideEnd)
        {
            c.SlideMudOff((f - SplatEnd) / (double)(SlideEnd - SplatEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;   // clear

        if (f <= FlyEnd && f > ThrowEnd)
        {
            // Pie in flight — a growing mud disc from the release point to centre.
            (int rx, int ry) = Hand(c, ThrowEnd);
            double p = (f - ThrowEnd) / (double)(FlyEnd - ThrowEnd);
            int bx = (int)Math.Round(SplashCanvas.Lerp(rx - 3, cx, p));
            int by = (int)Math.Round(SplashCanvas.Lerp(ry, cy, p));
            c.Blob(bx, by, 2 + (int)Math.Round(p * 4));
            return;
        }
        if (f > FlyEnd && f <= SplatEnd)
        {
            double p = (f - FlyEnd) / (double)(SplatEnd - FlyEnd);
            c.CoverMud(cx, cy, c.MaxRadius() * SplashCanvas.EaseOut(p));
            c.Flecks(cx, cy, c.MaxRadius() * SplashCanvas.EaseOut(p), 22);
            return;
        }

        // Arm holding the pie (enter → cock → throw). Pie sits in the fist — drawn
        // first so the hand grips over its right edge.
        (int hx, int hy) = Hand(c, f);
        DrawPie(c, hx - 6, hy);
        DrawArm(c, hx, hy);
    }

    // A thick, detailed arm: fist → forearm (skin) → sleeve filling to the right
    // edge (the elbow is off-screen). Rows hy-1..hy+1.
    private static void DrawArm(SplashCanvas c, int hx, int hy)
    {
        c.Str(hx, hy - 1, "▟█", Skin);        // fist gripping the pie
        c.Str(hx, hy,     "██", Skin);
        c.Str(hx, hy + 1, "▜█", Skin);
        c.Str(hx + 2, hy - 1, "████", Skin);  // forearm
        c.Str(hx + 2, hy,     "█████", Skin);
        c.Str(hx + 2, hy + 1, "████", Skin);
        int s = hx + 7;                        // sleeve / upper arm to the edge
        int w = Math.Max(2, c.Cols - s);
        for (int y = hy - 1; y <= hy + 1; y++) c.Str(s, y, new string('█', w), Sleeve);
    }

    // Hand position: slides in from the right, cocks back-and-up, then swings
    // forward past centre to release.
    private static (int X, int Y) Hand(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        if (f <= EnterEnd)
        {
            double t = SplashCanvas.Smooth(f / (double)EnterEnd);
            return ((int)Math.Round(SplashCanvas.Lerp(c.Cols + 6, cx + 9, t)), cy);
        }
        if (f <= CockEnd)
        {
            double t = SplashCanvas.Smooth((f - EnterEnd) / (double)(CockEnd - EnterEnd));
            return ((int)Math.Round(SplashCanvas.Lerp(cx + 9, cx + 12, t)), cy - (int)Math.Round(t * 2));
        }
        double u = (f - CockEnd) / (double)(ThrowEnd - CockEnd);
        return ((int)Math.Round(SplashCanvas.Lerp(cx + 12, cx + 1, u)),
                cy - 2 + (int)Math.Round(u * 2));
    }

    // A big round mud pie in a plate, anchored by its left edge at (x, y-centre).
    private static void DrawPie(SplashCanvas c, int x, int y)
    {
        c.Str(x + 1, y - 2, "▄▄▄▄▄", SplashCanvas.BrownLite);   // heaped mud dome
        c.Str(x,     y - 1, "▟▓▓▓▓▓▙", SplashCanvas.Brown);
        c.Str(x,     y,     "▐▓▓▓▓▓▌", SplashCanvas.Brown);
        c.Str(x,     y + 1, "▝▀▀▀▀▀▘", Plate);                  // tin
    }
}
