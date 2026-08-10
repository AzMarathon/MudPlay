using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A murky swamp rises into view and bubbles, then a lumpy mud monster surfaces —
// glowing eyes first — and lunges straight at us, growing closer and closer until
// its body engulfs the whole lens. Then the mud it left on the glass slides off.
public sealed class SwampMonsterScene : SplashScene
{
    public override int LoopFrames => 120;

    private const int SwampEnd = 14;   // murky surface rises into frame
    private const int LungeEnd = 58;   // monster surges in until it covers the lens
    private const int SlideEnd = 114;  // its mud slides off
    // 114..119: clear.

    private static readonly CellAttributes Bog   = SplashCanvas.Rgb(52, 62, 40);
    private static readonly CellAttributes BogLt = SplashCanvas.Rgb(78, 92, 54);
    private static readonly CellAttributes Eye   = SplashCanvas.Rgb(200, 240, 100);   // glowing

    public override void Render(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        if (f > LungeEnd && f <= SlideEnd)
        {
            // Slide the mud the monster smeared over the glass — what's on screen,
            // not a reconstructed splat.
            c.SlideSheetOff((f - LungeEnd) / (double)(SlideEnd - LungeEnd));
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Swamp surface rises from the bottom into the lower third.
        double rise = Math.Min(1.0, f / (double)SwampEnd);
        int surf = (int)Math.Round(SplashCanvas.Lerp(c.Rows, c.Rows * 0.64, SplashCanvas.Smooth(rise)));
        surf = Math.Max(surf, SplashCanvas.TitleRows + 2);
        for (int y = surf; y < c.Rows; y++)
        for (int x = 0; x < c.Cols; x++)
            c.Put(x, y, SplashCanvas.Noise(x, y) > 0.6 ? '▒' : '░',
                SplashCanvas.Noise(y, x) > 0.7 ? BogLt : Bog);
        for (int x = 0; x < c.Cols; x++)
            if (SplashCanvas.Noise(x, f / 3) > 0.7) c.Put(x, surf, 'o', BogLt);

        if (f < SwampEnd) return;

        // The monster: a solid mud mass that rises from the swamp and swells toward
        // the camera until it fills the lens. Eyes ride on it, growing closer, until
        // the body engulfs them.
        double p = SplashCanvas.EaseIn((f - SwampEnd) / (double)(LungeEnd - SwampEnd));
        double rad = SplashCanvas.Lerp(3, c.MaxRadius() * 1.2, p);
        int my = (int)Math.Round(SplashCanvas.Lerp(surf - 2, cy, p));
        MonsterMass(c, cx, my, rad);

        if (p < 0.8)
        {
            int eo = 1 + (int)Math.Round(rad * 0.35);
            int ey = my - (int)Math.Round(rad * 0.35);
            c.Put(cx - eo, ey, '◉', Eye);
            c.Put(cx + eo, ey, '◉', Eye);
        }
    }

    // A lumpy mud body (round despite 1:2 cells), painted with the canonical caked
    // mud so it hands straight off to the slide with no colour snap.
    private static void MonsterMass(SplashCanvas c, int mcx, int mcy, double rad)
    {
        int ri = (int)Math.Ceiling(rad);
        for (int y = mcy - ri; y <= mcy + ri; y++)
        for (int x = mcx - ri; x <= mcx + ri; x++)
        {
            double dx = x - mcx, dy = (y - mcy) * 2.0;
            double edge = rad * (0.86 + 0.28 * SplashCanvas.Noise(x, y));
            if (Math.Sqrt(dx * dx + dy * dy) > edge) continue;
            c.PutSheet(x, y, x, y);
        }
    }
}
