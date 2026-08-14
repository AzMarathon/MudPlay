using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A chocobo sprints across a grassy plain — the camera tracking alongside it, so
// the bird runs in place while distant hills, clouds and foreground grass scroll
// past to sell the speed. A mud puddle slides in from ahead; the chocobo faceplants
// straight into it, throwing up a splash, and the camera keeps rolling — leaving the
// upended bird behind in the mud as it scrolls off the left. Then the whole plain
// wipes off the lens, back to a clean frame for the seamless hand-off.
//
// The plain is painted as flat BACKGROUND fills (sky / hills / ground / clouds /
// puddle) so each same-colour span batches into one rectangle with no per-cell
// glyph — only the chocobo itself and the grass tufts cost real glyphs, which keeps
// this scene as cheap as the flattened mud ones.
public sealed class ChocoboScene : SplashScene
{
    public override int LoopFrames => 132;

    private const int IntroEnd  = 10;   // curtain opens L→R, revealing the plain
    private const int RunInEnd  = 24;   // chocobo has run in to its tracking spot
    private const int HitFrame  = 84;   // beak meets the puddle — faceplant
    private const int FallEnd   = 98;   // splash peaks, bird settled upended in mud
    private const int WipeStart = 116;  // fallen bird now off-screen left; wipe begins
    private const int WipeEnd   = 128;  // plain fully wiped
    // 128..131: clear.

    private const double CamSpeed = 1.5;   // cells the world scrolls per frame

    // ----- sky / ground (background fills) ----------------------------------
    private static readonly CellAttributes Sky        = Bg(64, 94, 134);
    private static readonly CellAttributes SkyHaze    = Bg(120, 150, 178);
    private static readonly CellAttributes Hill       = Bg(70, 116, 74);
    private static readonly CellAttributes GroundBack = Bg(96, 150, 74);
    private static readonly CellAttributes GroundFront= Bg(78, 128, 60);
    private static readonly CellAttributes CloudBg    = Bg(224, 231, 243);
    private static readonly CellAttributes PuddleBg   = Bg(92, 66, 40);
    private static readonly CellAttributes Shadow     = Bg(54, 92, 46);

    // ----- foreground detail (glyphs) ---------------------------------------
    private static readonly CellAttributes GrassLt = SplashCanvas.Rgb(154, 198, 98);
    private static readonly CellAttributes GrassDk = SplashCanvas.Rgb(96, 150, 70);
    private static readonly CellAttributes Rock    = SplashCanvas.Rgb(150, 142, 128);
    private static readonly CellAttributes Dust    = SplashCanvas.Rgb(186, 168, 132);
    private static readonly CellAttributes Rim     = SplashCanvas.Rgb(150, 116, 72);
    private static readonly CellAttributes Wipe    = SplashCanvas.Rgb(214, 224, 236);

    // ----- the chocobo -------------------------------------------------------
    private static readonly CellAttributes Yellow = SplashCanvas.Rgb(252, 216, 76);
    private static readonly CellAttributes YHi    = SplashCanvas.Rgb(255, 240, 158);
    private static readonly CellAttributes Gold   = SplashCanvas.Rgb(214, 166, 44);
    private static readonly CellAttributes Beak   = SplashCanvas.Rgb(246, 152, 42);
    private static readonly CellAttributes Leg    = SplashCanvas.Rgb(218, 128, 34);
    private static readonly CellAttributes Eye    = SplashCanvas.Rgb(40, 34, 44);
    private static readonly CellAttributes TailY  = SplashCanvas.Rgb(244, 202, 72);

    private static CellAttributes Bg(byte r, byte g, byte b) =>
        CellAttributes.Default.WithBackground(TerminalColor.Rgb(r, g, b));

    public override void Render(SplashCanvas c, int f)
    {
        int band = c.Rows - SplashCanvas.TitleRows;
        int horizonY = SplashCanvas.TitleRows + (int)Math.Round(band * 0.46);
        int midY = horizonY + (int)Math.Round((c.Rows - horizonY) * 0.5);
        int feetY = c.Rows - 3;
        double cameraX = f * CamSpeed;

        // Horizontal reveal (intro) / wipe (outro) window: [lo, hi). Frame 0 opens at
        // width 0 and the final wipe frame closes to 0, so both ends leave a clear lens.
        int revealR = f < IntroEnd
            ? (int)Math.Round(SplashCanvas.Lerp(0, c.Cols, f / (double)IntroEnd))
            : c.Cols;
        int wipeL = f >= WipeStart
            ? (int)Math.Round(SplashCanvas.Lerp(0, c.Cols, (f - WipeStart) / (double)(WipeEnd - 1 - WipeStart)))
            : 0;
        int lo = Math.Max(0, wipeL), hi = Math.Min(c.Cols, revealR);
        if (f >= WipeEnd || lo >= hi) return;   // clear

        void PB(int x, int y, CellAttributes a) { if (x >= lo && x < hi) c.PutBg(x, y, a); }
        void P(int x, int y, char ch, CellAttributes a) { if (x >= lo && x < hi) c.Put(x, y, ch, a); }
        void FillBand(int y0, int y1, CellAttributes a)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = lo; x < hi; x++) c.PutBg(x, y, a);
        }

        // Sky, with a hazier band just above the skyline.
        FillBand(SplashCanvas.TitleRows, horizonY - 1, Sky);
        FillBand(horizonY - 2, horizonY - 1, SkyHaze);

        // Rolling distant hills sitting on the skyline, slow parallax.
        double hillScroll = cameraX * 0.35;
        for (int x = lo; x < hi; x++)
        {
            double wx = x + hillScroll;
            double h = Math.Sin(wx * 0.15) * 0.5 + Math.Sin(wx * 0.37 + 1.3) * 0.32;
            int top = horizonY - 1 - (int)Math.Round((h + 0.82) / 1.64 * 3);
            for (int y = top; y < horizonY; y++) PB(x, y, Hill);
        }

        // Ground: lit grass to the horizon, richer green in the foreground.
        FillBand(horizonY, midY - 1, GroundBack);
        FillBand(midY, c.Rows - 1, GroundFront);

        // Clouds drifting in the upper sky, slowest parallax.
        int cwrap = c.Cols + 40;
        for (int i = 0; i < 5; i++)
        {
            int baseX = (int)(SplashCanvas.Noise(i, 51) * cwrap);
            int sx = ((baseX - (int)Math.Round(cameraX * 0.14)) % cwrap + cwrap) % cwrap - 20;
            int cy = SplashCanvas.TitleRows + 1 + (int)(SplashCanvas.Noise(i, 61) * Math.Max(1, horizonY - SplashCanvas.TitleRows - 4));
            for (int dx = 1; dx <= 2; dx++) PB(sx + dx, cy, CloudBg);
            for (int dx = 0; dx <= 3; dx++) PB(sx + dx, cy + 1, CloudBg);
        }

        // Foreground grass tufts + the odd pebble, scrolling at full camera speed.
        int gwrap = c.Cols + 24;
        for (int i = 0; i < 20; i++)
        {
            int baseX = (int)(SplashCanvas.Noise(i, 21) * gwrap);
            int sx = ((baseX - (int)Math.Round(cameraX)) % gwrap + gwrap) % gwrap - 12;
            int gy = horizonY + 1 + (int)(SplashCanvas.Noise(i, 31) * Math.Max(1, feetY - horizonY - 1));
            if (SplashCanvas.Noise(i, 41) > 0.82)
            {
                P(sx, gy, SplashCanvas.Noise(i, 5) > 0.5 ? '•' : '▖', Rock);
                continue;
            }
            CellAttributes gc = SplashCanvas.Noise(i, 3) > 0.5 ? GrassLt : GrassDk;
            P(sx, gy, '╲', gc); P(sx + 1, gy, '│', gc); P(sx + 2, gy, '╱', gc);
        }

        // Chocobo tracking spot vs. the approaching puddle. Before the hit the bird
        // holds screen-centre-left while the puddle slides in from the right; after
        // the hit the bird is pinned to the puddle's world-point, so both scroll off
        // together as the camera keeps rolling.
        int chocoHome = (int)Math.Round(c.Cols * 0.40);
        int puddleX = chocoHome + (int)Math.Round((HitFrame - f) * CamSpeed);
        int chocoX = f <= HitFrame
            ? (int)Math.Round(SplashCanvas.Lerp(-12, chocoHome, SplashCanvas.EaseOut(Math.Min(1.0, f / (double)RunInEnd))))
            : puddleX;

        // The puddle (drawn under the bird).
        DrawPuddle(puddleX, feetY);

        if (f < HitFrame)
        {
            int gait = (f / 2) % 2;
            int bob = f >= RunInEnd && gait == 1 ? -1 : 0;
            // soft ground shadow
            for (int dx = 0; dx <= 6; dx++) PB(chocoX + dx, feetY + 1, Shadow);
            DrawRun(chocoX, feetY - 9 + bob, gait);
        }
        else
        {
            DrawFallen(chocoX, feetY - 4);
            if (f <= FallEnd)   // splash erupts out of the puddle on impact
            {
                double p = (f - HitFrame) / (double)(FallEnd - HitFrame);
                c.Blob(puddleX + 6, feetY, 1 + (int)Math.Round(p * 3));
                c.Flecks(puddleX + 6, feetY - 1, 3 + p * 5, 10);
            }
        }

        // The wipe blade leading the outro sweep.
        if (f >= WipeStart)
            for (int y = SplashCanvas.TitleRows; y < c.Rows; y++) P(wipeL, y, '█', Wipe);

        // ----- sprites -------------------------------------------------------

        void DrawPuddle(int px, int gy)
        {
            for (int dx = -5; dx <= 5; dx++)
            {
                PB(px + dx, gy, PuddleBg);
                if (Math.Abs(dx) <= 3) PB(px + dx, gy + 1, PuddleBg);
            }
            P(px - 5, gy, '▁', Rim); P(px + 5, gy, '▁', Rim);
        }

        void DrawRun(int bx, int by, int gait)
        {
            // crest
            P(bx + 4, by, '╲', YHi); P(bx + 5, by, '│', YHi); P(bx + 6, by, '╱', YHi);
            // head
            P(bx + 3, by + 1, '▟', YHi); P(bx + 4, by + 1, '█', Yellow); P(bx + 5, by + 1, '█', Yellow); P(bx + 6, by + 1, '▙', Yellow);
            // face: eye + beak
            P(bx + 3, by + 2, '█', Yellow); P(bx + 4, by + 2, '█', Yellow); P(bx + 5, by + 2, '█', Eye); P(bx + 6, by + 2, '▜', Yellow); P(bx + 7, by + 2, '►', Beak);
            // jaw / throat
            P(bx + 3, by + 3, '▜', Yellow); P(bx + 4, by + 3, '█', Yellow); P(bx + 5, by + 3, '▛', Gold);
            // neck into body
            P(bx + 2, by + 4, '▗', Yellow); P(bx + 3, by + 4, '█', Yellow); P(bx + 4, by + 4, '█', Gold);
            // tail plume sweeping up behind
            P(bx - 2, by + 4, '╲', TailY); P(bx - 1, by + 5, '╲', TailY); P(bx - 1, by + 6, '≈', Gold);
            // body — back (highlit top edge)
            P(bx, by + 5, '▗', Yellow); P(bx + 1, by + 5, '▟', YHi);
            for (int x = 2; x <= 6; x++) P(bx + x, by + 5, '█', Yellow);
            P(bx + 7, by + 5, '▙', Yellow);
            // body — widest, with a folded wing
            for (int x = 0; x <= 7; x++) P(bx + x, by + 6, '█', x < 2 ? Gold : Yellow);
            P(bx + 2, by + 6, '╲', Gold); P(bx + 3, by + 6, '╲', Gold); P(bx + 8, by + 6, '▖', Yellow);
            // belly (shaded)
            P(bx + 1, by + 7, '▜', Gold); for (int x = 2; x <= 6; x++) P(bx + x, by + 7, '█', Gold); P(bx + 7, by + 7, '▛', Gold);
            // legs mid-stride + a puff of dust off the trailing foot
            if (gait == 0)
            {
                P(bx + 2, by + 8, '▙', Leg); P(bx + 1, by + 9, '◣', Leg);
                P(bx + 5, by + 8, '▟', Leg); P(bx + 6, by + 9, '◢', Leg);
                P(bx - 1, by + 9, '·', Dust); P(bx - 3, by + 9, '•', Dust);
            }
            else
            {
                P(bx + 3, by + 8, '█', Leg); P(bx + 3, by + 9, '▄', Leg);
                P(bx + 4, by + 8, '█', Leg); P(bx + 4, by + 9, '▄', Leg);
                P(bx - 2, by + 9, '·', Dust);
            }
        }

        void DrawFallen(int bx, int by)
        {
            // legs kicking skyward
            P(bx + 2, by, '╲', Leg); P(bx + 4, by, '│', Leg); P(bx + 6, by, '╱', Leg);
            // rump + tail up, left
            P(bx + 1, by + 1, '╲', TailY); P(bx + 2, by + 1, '▟', Yellow); P(bx + 3, by + 1, '█', Yellow); P(bx + 4, by + 1, '█', Yellow); P(bx + 5, by + 1, '▙', Gold);
            // body pitched forward into the mud
            P(bx + 2, by + 2, '▜', Gold); P(bx + 3, by + 2, '█', Yellow); P(bx + 4, by + 2, '█', Gold); P(bx + 5, by + 2, '█', Gold); P(bx + 6, by + 2, '▖', Yellow);
            // neck + head diving under, only the beak still clear
            P(bx + 5, by + 3, '▜', Yellow); P(bx + 6, by + 3, '█', Yellow); P(bx + 7, by + 3, '►', Beak);
        }
    }
}
