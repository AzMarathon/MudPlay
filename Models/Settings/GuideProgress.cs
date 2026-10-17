namespace MudPlay.Models.Settings;

// Where the user got to in each Visual Guide, keyed by guide id. Install-wide
// (Global tier): the guides teach the client, not a character.
public sealed class GuideProgress
{
    // The step index each unfinished guide was last left on.
    public Dictionary<string, int> StepReached { get; set; } = new(StringComparer.Ordinal);

    // Guides walked through to their Finish.
    public HashSet<string> Completed { get; set; } = new(StringComparer.Ordinal);
}
