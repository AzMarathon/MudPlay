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

    // ----- the chocobo (palette keyed off the reference sprite) --------------
    private static readonly CellAttributes Yellow = SplashCanvas.Rgb(248, 204, 64);   // body
    private static readonly CellAttributes YHi    = SplashCanvas.Rgb(255, 234, 132);  // lit back / crest
    private static readonly CellAttributes Gold   = SplashCanvas.Rgb(212, 152, 40);   // belly / wing shade
    private static readonly CellAttributes Amber  = SplashCanvas.Rgb(168, 108, 30);   // deep feather shade
    private static readonly CellAttributes Beak   = SplashCanvas.Rgb(240, 146, 36);   // hooked beak
    private static readonly CellAttributes Leg    = SplashCanvas.Rgb(176, 100, 36);   // legs
    private static readonly CellAttributes LegDk  = SplashCanvas.Rgb(120, 66, 28);    // claws / shade
    private static readonly CellAttributes Eye    = SplashCanvas.Rgb(56, 108, 200);   // blue eye
    private static readonly CellAttributes TailY  = SplashCanvas.Rgb(244, 198, 66);   // tail plume

    // Side sprite facing RIGHT (mirror of the reference art), coloured per region by
    // DrawBody: right-side cells are the head + hooked beak, the upper-left mass is
    // the big flowing tail plume, the centre is the body with a folded wing, and the
    // roots at the bottom feed the animated running legs (drawn separately).
    private static readonly string[] Body =
    {
        "╲ ╲      ╱▟█▙╲",   // 0: tail tips + crest tuft + head crown
        " ╲██▖    ▟███▙",   // 1: tail + head
        "╲████▙   ▐███►",   // 2: tail plume + head/eye + beak
        " ▜████▖  ▜██▛ ",   // 3: tail base + neck
        "  ▜██████████▖",   // 4: back (widest top)
        "  ▐██████████▌",   // 5: body + wing
        "   ▜████████▛ ",   // 6: belly (shaded)
        "    ██▘  ▝██  ",   // 7: body-bottom / leg roots
    };
    private static readonly string[] Fallen =
    {
        "  ╲▖ ▗╱ ",   // 0: tail feather + legs kicking up
        " ╲███▙╱ ",   // 1: tail plume standing up
        "  ▜███▙ ",   // 2: rump
        "  ▟███▖ ",   // 3: body pitching down
        " ▟███►  ",   // 4: head + beak diving into the mud
    };

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
        int chocoHome = (int)Math.Round(c.Cols * 0.38);
        const int BeakLead = 12;   // the beak sits this far right of the sprite anchor
        int puddleX = chocoHome + BeakLead + (int)Math.Round((HitFrame - f) * CamSpeed);
        int chocoX = (int)Math.Round(SplashCanvas.Lerp(-16, chocoHome, SplashCanvas.EaseOut(Math.Min(1.0, f / (double)RunInEnd))));

        // The puddle (drawn under the bird).
        DrawPuddle(puddleX, feetY);

        if (f < HitFrame)
        {
            int gait = (f / 2) % 2;
            int bob = f >= RunInEnd && gait == 1 ? -1 : 0;
            // soft ground shadow under the body
            for (int dx = 2; dx <= 12; dx++) PB(chocoX + dx, feetY + 1, Shadow);
            DrawRun(chocoX, feetY - 9 + bob, gait);
        }
        else
        {
            DrawFallen(puddleX - 4, feetY - 4);
            if (f <= FallEnd)   // splash erupts out of the puddle on impact
            {
                double p = (f - HitFrame) / (double)(FallEnd - HitFrame);
                c.Blob(puddleX, feetY, 1 + (int)Math.Round(p * 3));
                c.Flecks(puddleX, feetY - 1, 3 + p * 5, 10);
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

        // Paint a body template, colouring each cell by region: the beak glyph is
        // orange, the upper-left mass is the tail plume (alternating highlight/shade
        // feathers), the crown row is lit, the lower rows are the shaded belly, and
        // the rest is body yellow.
        void DrawBody(int bx, int by, string[] rows)
        {
            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                for (int i = 0; i < row.Length; i++)
                {
                    char ch = row[i];
                    if (ch == ' ') continue;
                    CellAttributes a =
                        ch == '►'            ? Beak
                        : i <= 5 && r <= 3   ? (((i + r) & 1) == 0 ? TailY : Amber)   // tail feathers
                        : r == 0             ? YHi                                     // lit crown
                        : r >= 6             ? Gold                                    // shaded belly
                        : Yellow;
                    P(bx + i, by + r, ch, a);
                }
            }
        }

        void DrawRun(int bx, int by, int gait)
        {
            DrawBody(bx, by, Body);
            // a long tail feather trailing off the plume, and a wisp above it
            P(bx - 1, by + 3, '╲', TailY); P(bx + 1, by + 0, '╲', Gold);
            // blue eye set into the head, toward the beak
            P(bx + 11, by + 2, '●', Eye);
            // legs mid-stride + a puff of dust off the trailing foot
            if (gait == 0)
            {
                P(bx + 5, by + 8, '▟', Leg); P(bx + 4, by + 9, '◣', LegDk);
                P(bx + 9, by + 8, '▙', Leg); P(bx + 10, by + 9, '◢', LegDk);
                P(bx + 2, by + 9, '·', Dust); P(bx + 0, by + 9, '•', Dust);
            }
            else
            {
                P(bx + 6, by + 8, '▐', Leg); P(bx + 6, by + 9, '▄', LegDk);
                P(bx + 8, by + 8, '▌', Leg); P(bx + 8, by + 9, '▄', LegDk);
                P(bx + 1, by + 9, '·', Dust);
            }
        }

        void DrawFallen(int bx, int by)
        {
            DrawBody(bx, by, Fallen);
            P(bx + 5, by + 0, '╲', TailY);   // a tail feather flicking over
            // legs kicking, drawn in leg colour over the template's kick glyphs
            P(bx + 2, by + 0, '╲', Leg); P(bx + 5, by + 0, '╱', Leg);
        }
    }
}
