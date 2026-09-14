using System;
using System.Text;

namespace MudPlay.ViewModels;

// The scrolling "UPDATE AVAILABLE" band that takes over the front of the window
// title while a newer build is waiting — a highway-sign crawl, so the notice is
// impossible to miss without stealing a window or interrupting play.
//
// Pure frame math: the caller owns the timer and hands in a monotonically rising
// frame number. Each frame starts one character earlier in an endlessly repeating
// band, which reads as the text sliding to the RIGHT.
public static class UpdateTitleMarquee
{
    // One repeat of the band. The trailing spaces are the gap between repeats, so
    // the crawl doesn't read as one unbroken run of exclamation marks.
    private const string Unit = "▶ UPDATE AVAILABLE!!!!!!  ";

    // How much of the band is on screen at once. Wide enough to show the message
    // twice over, short enough to leave the character + BBS readable on the end.
    private const int Width = 52;

    public static string Frame(int frame)
    {
        // Negative frames would throw on Substring; a caller counting down is a bug,
        // but the title bar is the last place worth crashing over.
        int offset = ((frame % Unit.Length) + Unit.Length) % Unit.Length;
        int start = (Unit.Length - offset) % Unit.Length;

        StringBuilder band = new(Unit.Length + Width);
        while (band.Length < start + Width) band.Append(Unit);
        return band.ToString(start, Width);
    }

    // How many frames before the crawl repeats itself. Lets a caller keep its counter
    // bounded instead of letting it run to int.MaxValue over a long session.
    public static int Period => Unit.Length;
}
