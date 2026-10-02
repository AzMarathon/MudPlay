using System.Text;

namespace MudPlay.ViewModels.StatusBar;

// The crawl a marquee status-bar row shows: the row's text repeated end to end, one
// character further along each frame, so it reads as sliding left. Pure frame math;
// the caller owns the timer and the frame counter.
public static class StatusBarMarquee
{
    // The space between one pass of the text and the next.
    public const string Gap = "     ";

    // Long enough to fill any window; the text block clips the rest.
    private const int Width = 400;

    public static string Frame(string text, int frame)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string unit = text + Gap;
        int start = ((frame % unit.Length) + unit.Length) % unit.Length;

        StringBuilder band = new(start + Width + unit.Length);
        while (band.Length < start + Width) band.Append(unit);
        return band.ToString(start, Width);
    }
}
