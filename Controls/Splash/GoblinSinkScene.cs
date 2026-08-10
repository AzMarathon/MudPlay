using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Bird's-eye view: a goblin lies sprawled in a mud pool and very slowly sinks
// under the surface, mud creeping over him limb by limb until he's gone — then
// the whole pool is wiped off the lens.
public sealed class GoblinSinkScene : SplashScene
{
    public override int LoopFrames => 132;

    private const int RevealEnd = 10;   // pool + goblin wipe into view
    private const int SinkEnd   = 88;   // goblin very slowly goes under (the long beat)
    private const int SlideEnd  = 126;  // pool wiped off
    // 126..131: clear.

    private static readonly CellAttributes Pool   = SplashCanvas.Rgb(74, 66, 40);
    private static readonly CellAttributes PoolLt   = SplashCanvas.Rgb(98, 92, 54);
    private static readonly CellAttributes Gob      = SplashCanvas.Rgb(96, 150, 70);
    private static readonly CellAttributes GobDk     = SplashCanvas.Rgb(66, 110, 48);
    private static readonly CellAttributes Eye       = SplashCanvas.Rgb(220, 220, 120);

    // Top-down goblin, arms out, legs down; anchored by its centre column.
    private static readonly string[] Goblin =
    {
        "  ╲▄╱  ",   // pointy ears / hair
        " ▟███▙ ",   // head
        " █▘ ▝█ ",   // eyes row (eyes drawn separately)
        "◄██████►",  // shoulders + arms out
        "  ████  ",  // torso
        "  █  █  ",  // legs
        "  ▘  ▝  ",  // feet
    };

    public override void Render(SplashCanvas c, int f)
    {
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        if (f > SinkEnd && f <= SlideEnd)
        {
            // Clear the filled pool from the centre outwards.
            c.ClearFromCenter((f - SinkEnd) / (double)(SlideEnd - SinkEnd));
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Mud pool fills the whole band, wiping in top-down over the reveal.
        double reveal = Math.Min(1.0, f / (double)RevealEnd);
        int poolBot = SplashCanvas.TitleRows + (int)Math.Round(reveal * (c.Rows - SplashCanvas.TitleRows));
        for (int y = SplashCanvas.TitleRows; y < poolBot; y++)
        for (int x = 0; x < c.Cols; x++)
            c.Put(x, y, SplashCanvas.Noise(x, y) > 0.55 ? '▒' : '░',
                SplashCanvas.Noise(y, x) > 0.7 ? PoolLt : Pool);

        if (f < RevealEnd) return;

        // Goblin lying in the pool; mud creeps over him as he sinks. sink 0→1.
        double sink = Math.Clamp((f - RevealEnd) / (double)(SinkEnd - RevealEnd), 0, 1);
        int gx = cx - Goblin[0].Length / 2;
        int gy = cy - Goblin.Length / 2;
        for (int r = 0; r < Goblin.Length; r++)
        {
            string row = Goblin[r];
            for (int i = 0; i < row.Length; i++)
            {
                char ch = row[i];
                if (ch == ' ') continue;
                int x = gx + i, y = gy + r;
                // Each cell submerges at its own moment (feet-first bias + noise), so
                // the mud swallows him raggedly rather than as a clean line.
                double bias = 0.15 + 0.5 * (r / (double)Goblin.Length) + 0.35 * SplashCanvas.Noise(x, y);
                if (sink > bias) continue;                       // gone under — pool shows
                CellAttributes a = ch is '◄' or '►' or '█' && r == 3 ? GobDk : Gob;
                c.Put(x, y, ch, a);
            }
        }
        // Eyes, until the head sinks.
        if (sink < 0.6)
        {
            c.Put(gx + 2, gy + 2, '●', Eye);
            c.Put(gx + 4, gy + 2, '●', Eye);
        }
        // A couple of bubbles surface as he goes under.
        if (sink > 0.4)
            for (int b = 0; b < 3; b++)
                if (SplashCanvas.Noise(b, f / 4) > 0.6)
                    c.Put(cx - 2 + b * 2, cy - (f / 6 % 3), 'o', PoolLt);
    }
}
