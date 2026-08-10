using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A figure in our line of sight rises into view, winds up, and hurls a mud ball
// at the lens like an overhand pitch; it splats across the view, then slides
// slowly down and off the bottom, leaving a clean lens. (Ported from the original
// single splash, re-bookended so it starts and ends clear for seamless swapping.)
public sealed class MudThrowScene : SplashScene
{
    public override int LoopFrames => 132;

    private const int RiseEnd   = 8;    // figure rises up from below
    private const int WindupEnd = 20;   // arm draws up and back
    private const int ThrowEnd  = 26;   // overhand swing; ball leaves the hand
    private const int FlyEnd    = 34;   // ball grows as it flies at the camera
    private const int SplatEnd  = 42;   // mud floods the viewport
    private const int SlideEnd  = 126;  // it slides down and off (slow)
    // 126..131: clear lens.

    private static readonly CellAttributes Figure    = SplashCanvas.Rgb(66, 56, 46);
    private static readonly CellAttributes FigureLit = SplashCanvas.Rgb(98, 84, 66);

    public override void Render(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        if (f <= SplatEnd && f > FlyEnd)          // mud flooding the lens
        {
            double r = c.MaxRadius() * SplashCanvas.EaseOut((f - FlyEnd) / (double)(SplatEnd - FlyEnd));
            c.CoverMud(cx, cy, r);
            c.Flecks(cx, cy, r);
            return;
        }
        if (f > SplatEnd && f <= SlideEnd)         // mud running off → clean lens
        {
            c.SlideMudOff((f - SplatEnd) / (double)(SlideEnd - SplatEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;                  // clear

        // Figure phases (rise → windup → throw → fly).
        int yOff = RiseOffset(c, f);
        if (f < ThrowEnd) DrawFigure(c, HandThrow(c, f), ballAtHand: f >= RiseEnd && f < ThrowEnd - 1, yOff);
        else { DrawFigure(c, NeutralHand(c), ballAtHand: false, yOff); DrawFlyingBall(c, f); }
    }

    // Figure slides up from off the bottom over the rise phase, then sits.
    private static int RiseOffset(SplashCanvas c, int f)
    {
        if (f >= RiseEnd) return 0;
        double t = SplashCanvas.Smooth(f / (double)RiseEnd);
        return (int)Math.Round((1 - t) * (c.Rows - SplashCanvas.TitleRows + 4));
    }

    private static int FootRow(SplashCanvas c) => c.Rows - 2;

    private static (int X, int Y) NeutralHand(SplashCanvas c)
    {
        int cx = c.Cols / 2, foot = FootRow(c);
        return (cx + 3, foot - 4);
    }

    private static (int X, int Y) HandThrow(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, foot = FootRow(c);
        if (f <= WindupEnd)
        {
            double t = Math.Clamp((f - RiseEnd) / (double)(WindupEnd - RiseEnd), 0, 1);
            return (cx + 3 + (int)Math.Round(t), foot - 4 - (int)Math.Round(t * 5));
        }
        double u = (f - WindupEnd) / (double)(ThrowEnd - WindupEnd);
        int hx = (int)Math.Round(SplashCanvas.Lerp(cx + 4, cx - 1, u));
        int hy = (int)Math.Round(SplashCanvas.Lerp(foot - 9, foot - 7, u) - Math.Sin(u * Math.PI) * 1.5);
        return (hx, hy);
    }

    private static void DrawFigure(SplashCanvas c, (int X, int Y) hand, bool ballAtHand, int yOff)
    {
        int cx = c.Cols / 2, foot = FootRow(c) + yOff;
        if (foot - 9 < SplashCanvas.TitleRows - 12) return;

        c.Str(cx - 2, foot - 8, "▄██▄", Figure);
        c.Str(cx - 2, foot - 7, "▐██▌", Figure);
        c.Str(cx - 3, foot - 6, "▄████▄", Figure);
        c.Str(cx - 2, foot - 5, "████", FigureLit);
        c.Str(cx - 2, foot - 4, "████", Figure);
        c.Str(cx - 2, foot - 3, "▐██▌", Figure);
        c.Str(cx - 2, foot - 2, "█  █", Figure);
        c.Str(cx - 2, foot - 1, "▀  ▀", Figure);

        c.Line(cx - 3, foot - 6, cx - 4, foot - 3, '▓', Figure);   // left arm
        c.Put(cx - 4, foot - 3, '▄', Figure);

        c.Line(cx + 2, foot - 6, hand.X, hand.Y + yOff, '▓', FigureLit);   // throwing arm
        if (ballAtHand)
        {
            c.Put(hand.X, hand.Y + yOff, '●', SplashCanvas.BrownLite);
            c.Put(hand.X, hand.Y + yOff - 1, '▒', SplashCanvas.Brown);
        }
    }

    private static void DrawFlyingBall(SplashCanvas c, int f)
    {
        (int rx, int ry) = HandThrow(c, ThrowEnd);
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        double p = (f - ThrowEnd) / (double)(FlyEnd - ThrowEnd);
        int bx = (int)Math.Round(SplashCanvas.Lerp(rx, cx, p));
        int by = (int)Math.Round(SplashCanvas.Lerp(ry, cy, p));
        c.Blob(bx, by, 1 + (int)Math.Round(p * 4));
    }
}
