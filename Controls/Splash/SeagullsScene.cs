using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Rows of seagulls perched on wires, staring at us. "Mud!" speech bubbles pop up
// sporadically over random birds (a nod to "Mine! Mine!") — until mud pours down
// from the sky and buries the whole flock, then slides off. Squawk.
public sealed class SeagullsScene : SplashScene
{
    public override int LoopFrames => 132;

    private const int RevealEnd = 10;   // gulls rise onto the wires
    private const int MudFrame  = 60;    // mud starts pouring from the sky
    private const int CoverEnd  = 82;    // flock fully buried
    private const int SlideEnd  = 126;   // slides off
    // 126..131: clear.

    private static readonly CellAttributes Gull   = SplashCanvas.Rgb(236, 238, 242);
    private static readonly CellAttributes Wing     = SplashCanvas.Rgb(150, 158, 168);
    private static readonly CellAttributes Beak      = SplashCanvas.Rgb(236, 150, 40);
    private static readonly CellAttributes Eye       = SplashCanvas.Rgb(28, 28, 34);
    // The wire is a flat background strip, not a row of '━' glyphs: a full-width
    // glyph line was ~Cols FormattedText per wire per frame (two wires = the bulk of
    // this scene's per-frame glyph cost), whereas a same-colour bg span batches into
    // one rectangle with no glyphs. Reads as a perch just the same.
    private static readonly CellAttributes WireBg = CellAttributes.Default.WithBackground(TerminalColor.Rgb(96, 90, 80));
    private static readonly CellAttributes Bubble    = SplashCanvas.Rgb(240, 240, 236);

    public override void Render(SplashCanvas c, int f)
    {
        if (f > CoverEnd && f <= SlideEnd)
        {
            c.SlideSheetOff((f - CoverEnd) / (double)(SlideEnd - CoverEnd));
            return;
        }
        if (f > SlideEnd) return;   // clear

        int yOff = f < RevealEnd
            ? (int)Math.Round((1 - SplashCanvas.Smooth(f / (double)RevealEnd)) * (c.Rows - SplashCanvas.TitleRows))
            : 0;

        int mid = (c.Rows + SplashCanvas.TitleRows) / 2;
        int[] wires = { mid - 3 + yOff, mid + 4 + yOff };
        int spacing = 9;
        int startX = 4;

        for (int r = 0; r < wires.Length; r++)
        {
            int wireY = wires[r] + 3;                       // feet rest here
            c.FillBg(0, wireY, c.Cols - 1, wireY, WireBg);
            for (int gx = startX, i = 0; gx < c.Cols - 4; gx += spacing, i++)
            {
                DrawGull(c, gx, wires[r]);
                // Sporadic "Mud!" — each gull re-rolls every few frames, so bubbles
                // pop in and out over the flock (stops once the mud starts falling).
                int id = r * 100 + i;
                if (f < MudFrame && f > RevealEnd && SplashCanvas.Noise(id, f / 7) > 0.73)
                    DrawBubble(c, gx - 1, wires[r] - 4);
            }
        }

        // "Mud!" answered — mud pours down from the sky and buries the flock.
        if (f >= MudFrame)
            c.FillFromTop((f - MudFrame) / (double)(CoverEnd - MudFrame));
    }

    private static void DrawGull(SplashCanvas c, int gx, int gy)
    {
        c.Str(gx, gy, "▟█▙", Gull);          // head
        c.Put(gx, gy + 1, '●', Eye);         // eyes + beak
        c.Put(gx + 2, gy + 1, '●', Eye);
        c.Put(gx + 1, gy + 1, '▼', Beak);
        c.Str(gx - 1, gy + 2, "▟███▙", Gull); // body
        c.Put(gx - 1, gy + 2, '◣', Wing);     // wing tips
        c.Put(gx + 3, gy + 2, '◢', Wing);
        c.Put(gx + 1, gy + 3, '╻', Beak);     // feet on the wire
    }

    private static void DrawBubble(SplashCanvas c, int bx, int by)
    {
        c.Str(bx, by,     "╭────╮", Bubble);
        c.Str(bx, by + 1, "│Mud!│", Bubble);
        c.Str(bx, by + 2, "╰─┬──╯", Bubble);
    }
}
