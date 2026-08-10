using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A stern man in a hat rises up, jabs a finger at his open palm, and a speech
// bubble demands "Mud Now." — so mud duly splats into his hand and floods the
// lens, then slides off. (An affectionate nod to the pointing-at-palm meme.)
public sealed class MudNowScene : SplashScene
{
    public override int LoopFrames => 124;

    private const int RevealEnd = 10;   // man rises into view
    private const int MudFrame  = 48;   // mud arrives in the palm
    private const int CoverEnd  = 62;   // it splashes up and floods the lens
    private const int SlideEnd  = 118;  // slides off
    // 118..123: clear.

    private static readonly CellAttributes Hat    = SplashCanvas.Rgb(206, 190, 150);
    private static readonly CellAttributes Band    = SplashCanvas.Rgb(120, 100, 70);
    private static readonly CellAttributes Face     = SplashCanvas.Rgb(216, 176, 140);
    private static readonly CellAttributes Brow     = SplashCanvas.Rgb(120, 84, 60);
    private static readonly CellAttributes Coat     = SplashCanvas.Rgb(188, 168, 120);
    private static readonly CellAttributes CoatDk    = SplashCanvas.Rgb(150, 132, 92);
    private static readonly CellAttributes Bubble    = SplashCanvas.Rgb(240, 240, 236);

    public override void Render(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        if (f > CoverEnd && f <= SlideEnd)
        {
            c.SlideMudOff((f - CoverEnd) / (double)(SlideEnd - CoverEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Everything sits around a chest/table line; the whole figure rises in.
        int armY = cy + 3;
        int yOff = f < RevealEnd
            ? (int)Math.Round((1 - SplashCanvas.Smooth(f / (double)RevealEnd)) * (c.Rows - SplashCanvas.TitleRows))
            : 0;
        int mx = cx - 11;          // man's centre (left of frame)
        int palmX = cx + 4;        // open palm (right)

        DrawMan(c, mx, armY + yOff);
        DrawArmAndPalm(c, mx, palmX, armY + yOff);
        DrawBubble(c, mx, armY + yOff);

        // Demand met: a mud gob drops into the palm, then splashes up and floods the
        // lens (its centre rides from the palm to mid-view as it swells).
        if (f >= MudFrame)
        {
            double p = (f - MudFrame) / (double)(CoverEnd - MudFrame);
            if (p < 0.35) c.Blob(palmX, (armY + yOff) - 1 - (int)Math.Round((0.35 - p) * 18), 2);
            double e = SplashCanvas.EaseOut(p);
            int scx = (int)Math.Round(SplashCanvas.Lerp(palmX, cx, e));
            int scy = (int)Math.Round(SplashCanvas.Lerp(armY + yOff, cy, e));
            c.CoverMud(scx, scy, c.MaxRadius() * e * 1.15);
            c.Flecks(scx, scy, c.MaxRadius() * e, 18);
        }
    }

    private static void DrawMan(SplashCanvas c, int mx, int baseY)
    {
        // Hat, stern face, coat — a bust, anchored so baseY is the shoulders.
        c.Str(mx - 3, baseY - 8, "▄▄▄▄▄▄▄", Hat);      // brim top
        c.Str(mx - 2, baseY - 7, "▟█████▙", Hat);      // crown
        c.Str(mx - 2, baseY - 6, "███████", Band);     // band
        c.Str(mx - 4, baseY - 5, "▄███████▄", Hat);    // wide brim
        c.Str(mx - 2, baseY - 4, "▐▀▀▀▀▀▌", Face);      // forehead
        c.Str(mx - 2, baseY - 3, "▜▄▄▄▄▄▛", Brow);      // furrowed brow (angry)
        c.Str(mx - 2, baseY - 2, "▐●▐▐●▌", Face);       // eyes
        c.Str(mx - 2, baseY - 1, "▐ ╻╻ ▌", Face);       // scowl
        c.Str(mx - 3, baseY,     "▟███████▙", Coat);    // collar / shoulders
        c.Str(mx - 3, baseY + 1, "█████████", CoatDk);  // coat
    }

    private static void DrawArmAndPalm(SplashCanvas c, int mx, int palmX, int armY)
    {
        // Sleeve reaching right from the shoulder along the table.
        c.Str(mx + 5, armY, "████", Coat);
        c.Str(mx + 5, armY + 1, "▀▀▀▀", CoatDk);
        // The pointing hand + finger tapping the palm.
        c.Str(mx + 9, armY, "▐▊", Face);
        c.Put(palmX - 1, armY, '▸', Face);       // index finger
        // The open palm.
        c.Str(palmX, armY, "▂▂▂", Face);
        c.Str(palmX, armY + 1, "╰─╯", Face);
    }

    private static void DrawBubble(SplashCanvas c, int mx, int baseY)
    {
        int bx = mx + 6, by = baseY - 11;        // above and right of the head
        c.Str(bx, by,     "╭────────╮", Bubble);
        c.Str(bx, by + 1, "│Mud Now.│", Bubble);
        c.Str(bx, by + 2, "╰──┬─────╯", Bubble);
        c.Put(bx + 2, by + 3, '╲', Bubble);      // tail toward the man
    }
}
